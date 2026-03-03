using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Services.Core.Services
{
    public class LogManager
    {
        private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "windows_service_logs");
        private const int DefaultRetentionDays = 7;

        public LogManager()
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
        }

        public string GetLogDirectory()
        {
            return LogDirectory;
        }

        public string? GetLatestLogPath(string serviceName)
        {
            if (!Directory.Exists(LogDirectory)) return null;

            var logFile = Directory.GetFiles(LogDirectory, $"{serviceName}_*.log")
                                   .OrderByDescending(f => File.GetCreationTime(f))
                                   .FirstOrDefault();
            
            if (logFile != null) return logFile;

            return null;
        }

        public async Task<string> ReadLogAsync(string? logPath)
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return "未找到日志文件。";

            try
            {
                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length > 100 * 1024)
                    {
                        stream.Seek(-100 * 1024, SeekOrigin.End);
                        // StreamReader buffers data, so we must seek BEFORE creating it.
                        using (var reader = new StreamReader(stream))
                        {
                            await reader.ReadLineAsync(); // Discard partial line
                            return "... (旧日志已截断) ...\n" + await reader.ReadToEndAsync();
                        }
                    }
                    
                    using (var reader = new StreamReader(stream))
                    {
                        return await reader.ReadToEndAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                return $"读取日志错误: {ex.Message}";
            }
        }

        public void CleanupOldLogs(int retentionDays = DefaultRetentionDays)
        {
            if (!Directory.Exists(LogDirectory)) return;

            try
            {
                var cutoffDate = DateTime.Now.AddDays(-retentionDays);
                var files = Directory.GetFiles(LogDirectory, "*.log");

                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.CreationTime < cutoffDate)
                        {
                            fileInfo.Delete();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to delete log {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CleanupOldLogs failed: {ex.Message}");
            }
        }

        public static int GetGlobalRetentionDays()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WindowsServiceManager");
                if (key != null)
                {
                    var val = key.GetValue("LogRetentionDays");
                    if (val is int days) return days;
                }
            }
            catch { }
            return DefaultRetentionDays;
        }

        public static void SetGlobalRetentionDays(int days)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WindowsServiceManager");
                key.SetValue("LogRetentionDays", days);
            }
            catch { }
        }
    }
}
