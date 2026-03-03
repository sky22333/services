using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;

namespace Services.Core.Services
{
    public class EmbeddedServiceWrapper : ServiceBase
    {
        private Process? _process;
        private string _serviceName;
        private StreamWriter? _logWriter;
        private object _logLock = new object();
        private int _retentionDays = 7;

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

                CleanupOldLogs();

                StartTargetProcess(config);
            }
            catch (Exception ex)
            {
                LogCriticalError(ex);
                ExitCode = 1064;
                Stop();
            }
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
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill();
                    _process.WaitForExit(5000);
                }
                catch { }
            }
            
            CloseLogWriter();
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
                
                using var globalKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WindowsServiceManager");
                if (globalKey != null)
                {
                    var val = globalKey.GetValue("LogRetentionDays");
                    if (val is int days) return days;
                }
            }
            catch { }
            return 7;
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
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "windows_service_logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"{_serviceName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            var fs = new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            _logWriter = new StreamWriter(fs) { AutoFlush = true };

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

                _process.OutputDataReceived += (s, e) => WriteLog(e.Data);
                _process.ErrorDataReceived += (s, e) => WriteLog("ERROR: " + e.Data);

                if (!_process.Start())
                {
                    throw new Exception("Process.Start() returned false.");
                }

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                _process.EnableRaisingEvents = true;
                _process.Exited += (s, e) => 
                {
                     WriteLog($"Target process exited with code: {_process.ExitCode}");
                     Stop();
                };
            }
            catch (Exception ex)
            {
                WriteLog($"CRITICAL: Failed to start target process. {ex.Message}");
                throw;
            }
        }

        private void WriteLog(string? message)
        {
            if (message == null) return;
            lock (_logLock)
            {
                try
                {
                    if (_logWriter != null)
                    {
                        _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                    }
                }
                catch { }
            }
        }

        private void CloseLogWriter()
        {
            lock (_logLock)
            {
                if (_logWriter != null)
                {
                    try { _logWriter.Close(); } catch { }
                    _logWriter = null;
                }
            }
        }
    }
}
