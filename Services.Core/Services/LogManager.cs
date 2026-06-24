using System;
using System.IO;
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

            string? latestFile = null;
            DateTime latestTime = DateTime.MinValue;

            foreach (var file in Directory.EnumerateFiles(LogDirectory, $"{serviceName}_*.log"))
            {
                try
                {
                    var creationTime = File.GetCreationTime(file);
                    if (creationTime > latestTime)
                    {
                        latestTime = creationTime;
                        latestFile = file;
                    }
                }
                catch { }
            }

            return latestFile;
        }

        public void CleanupOldLogs(int retentionDays = DefaultRetentionDays)
        {
            if (!Directory.Exists(LogDirectory)) return;

            try
            {
                var cutoffDate = DateTime.Now.AddDays(-retentionDays);

                foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log"))
                {
                    try
                    {
                        if (File.GetCreationTime(file) < cutoffDate)
                        {
                            File.Delete(file);
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
