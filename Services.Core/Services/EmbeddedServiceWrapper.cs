using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Services.Core.Helpers;

namespace Services.Core.Services
{
    public class EmbeddedServiceWrapper : ServiceBase
    {
        private Process? _process;
        private string _serviceName;
        private AsyncLogger? _logger;
        private bool _autoRestart = false;
        private int _restartDelayMs = 5000;
        private bool _isStopping = false;
        private int _restartCount = 0;
        private DateTime _lastRestartTime = DateTime.MinValue;
        private const int MaxRestarts = 5;

        public EmbeddedServiceWrapper(string serviceName)
        {
            _serviceName = serviceName;
            ServiceName = serviceName;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                var config = LoadConfig();
                _autoRestart = LoadAutoRestart();

                InitLogger();
                StartTargetProcess(config);
            }
            catch (Exception ex)
            {
                LogCriticalError(ex);
                ExitCode = 1064;
                Stop();
            }
        }

        private void InitLogger()
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "windows_service_logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"{_serviceName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _logger = new AsyncLogger(logFile);
        }

        private void LogCriticalError(Exception ex)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "windows_service_logs");
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir, $"{_serviceName}_CRASH_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(logFile, $"Service Startup Failed:\n{ex}");
            }
            catch { }
        }

        protected override void OnStop()
        {
            _isStopping = true;
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill(true);
                    _process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    _logger?.Log($"Error stopping process: {ex.Message}");
                }
            }

            _process?.Dispose();
            _process = null;

            _logger?.Dispose();
            _logger = null;
        }

        private (string ExePath, string Args, string WorkingDir) LoadConfig()
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{_serviceName}\Parameters");
            if (key == null) throw new Exception("Service configuration not found in registry");

            var exePath = key.GetValue("ExePath") as string;
            var args = key.GetValue("Args") as string;
            var workingDir = key.GetValue("WorkingDir") as string;

            if (string.IsNullOrEmpty(exePath)) throw new Exception("ExePath is missing in registry");

            return (exePath, args ?? "", workingDir ?? "");
        }

        private bool LoadAutoRestart()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{_serviceName}\Parameters");
                if (key != null)
                {
                    var val = key.GetValue("AutoRestart");
                    if (val is int v) return v == 1;
                }
            }
            catch { }
            return false;
        }

        private void StartTargetProcess((string ExePath, string Args, string WorkingDir) config)
        {
            try
            {
                _process?.Dispose();

                var psi = new ProcessStartInfo
                {
                    FileName = config.ExePath,
                    Arguments = config.Args,
                    WorkingDirectory = string.IsNullOrEmpty(config.WorkingDir) ? (Path.GetDirectoryName(config.ExePath) ?? "") : config.WorkingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _process = new Process { StartInfo = psi };

                _process.OutputDataReceived += (s, e) => { if (e.Data != null) _logger?.Log(e.Data); };
                _process.ErrorDataReceived += (s, e) => { if (e.Data != null) _logger?.Log("ERROR: " + e.Data); };

                if (!_process.Start())
                {
                    throw new Exception("Process.Start() returned false.");
                }

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                _process.EnableRaisingEvents = true;
                _process.Exited += (s, e) =>
                {
                    int exitCode = _process.ExitCode;
                    _logger?.Log($"Process exited (code: {exitCode})");

                    if (_isStopping) return;

                    if (!_autoRestart || exitCode == 0)
                    {
                        _logger?.Log(exitCode == 0 ? "Normal exit, not restarting" : "AutoRestart disabled");
                        Stop();
                        return;
                    }

                    if ((DateTime.Now - _lastRestartTime).TotalMinutes > 10)
                        _restartCount = 0;

                    if (++_restartCount > MaxRestarts)
                    {
                        _logger?.Log($"Max restarts ({MaxRestarts}) exceeded. Stopping.");
                        Stop();
                        return;
                    }

                    int delay = _restartDelayMs << Math.Min(_restartCount - 1, 4);
                    _lastRestartTime = DateTime.Now;

                    _logger?.Log($"Restart {_restartCount}/{MaxRestarts} in {delay}ms");
                    Task.Delay(delay).ContinueWith(_ =>
                    {
                        if (!_isStopping) StartTargetProcess(config);
                    });
                };
            }
            catch (Exception ex)
            {
                _logger?.Log($"Failed to start: {ex.Message}");

                if (!_autoRestart) throw;

                if ((DateTime.Now - _lastRestartTime).TotalMinutes > 10)
                    _restartCount = 0;

                if (++_restartCount > MaxRestarts)
                {
                    _logger?.Log($"Max restarts ({MaxRestarts}) exceeded. Stopping.");
                    throw;
                }

                int delay = _restartDelayMs << Math.Min(_restartCount - 1, 4);
                _lastRestartTime = DateTime.Now;

                _logger?.Log($"Retry {_restartCount}/{MaxRestarts} in {delay}ms");
                Task.Delay(delay).ContinueWith(_ =>
                {
                    if (!_isStopping) StartTargetProcess(config);
                });
            }
        }
    }
}
