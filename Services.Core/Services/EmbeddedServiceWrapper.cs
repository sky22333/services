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
        private int _retentionDays = 7;
        private bool _autoRestart = false;
        private int _restartDelayMs = 5000;
        private bool _isStopping = false;

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

                _retentionDays = LoadRetentionDays();
                _autoRestart = LoadAutoRestart();

                CleanupOldLogs();
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
                    _process.Kill(true); // Kill entire process tree
                    _process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    _logger?.Log($"Error stopping process: {ex.Message}");
                }
            }

            _logger?.Dispose();
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

        private int LoadRetentionDays()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{_serviceName}\Parameters");
                if (key != null)
                {
                    var val = key.GetValue("LogRetentionDays");
                    if (val is int days) return days;
                }
            }
            catch { }
            return 7;
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

        private void CleanupOldLogs()
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "windows_service_logs");
            if (!Directory.Exists(logDir)) return;

            try
            {
                var cutoffDate = DateTime.Now.AddDays(-_retentionDays);
                var files = Directory.GetFiles(logDir, $"{_serviceName}_*.log");

                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    if (info.CreationTime < cutoffDate)
                    {
                        try { info.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        private void StartTargetProcess((string ExePath, string Args, string WorkingDir) config)
        {
            try
            {
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
                    _logger?.Log($"Target process exited with code: {_process.ExitCode}");
                    
                    if (_isStopping) return;

                    if (_autoRestart)
                    {
                         _logger?.Log($"Auto-restart enabled. Restarting in {_restartDelayMs}ms...");
                         Task.Delay(_restartDelayMs).ContinueWith(_ => 
                         {
                             if (!_isStopping) StartTargetProcess(config);
                         });
                    }
                    else
                    {
                        Stop();
                    }
                };
            }
            catch (Exception ex)
            {
                _logger?.Log($"CRITICAL: Failed to start target process. {ex.Message}");
                if (!_autoRestart) throw;
                
                 Task.Delay(_restartDelayMs).ContinueWith(_ => 
                 {
                     if (!_isStopping) StartTargetProcess(config);
                 });
            }
        }
    }
}
