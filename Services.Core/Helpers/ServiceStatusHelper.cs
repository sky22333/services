using System;
using System.Runtime.InteropServices;

namespace Services.Core.Helpers
{
    /// <summary>
    /// Provides shared functionality for querying Windows service status
    /// </summary>
    public static class ServiceStatusHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr hService, int infoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

        /// <summary>
        /// Queries the current status of a Windows service
        /// </summary>
        /// <param name="hService">Handle to the service</param>
        /// <returns>Tuple containing status string and process ID</returns>
        public static (string Status, int Pid) QueryStatus(IntPtr hService)
        {
            if (hService == IntPtr.Zero) return ("未知", 0);

            try
            {
                // Use stackalloc for better performance (no heap allocation)
                Span<byte> buffer = stackalloc byte[1024];
                unsafe
                {
                    fixed (byte* pBuffer = buffer)
                    {
                        if (QueryServiceStatusEx(hService, 0, (IntPtr)pBuffer, 1024, out uint bytesNeeded))
                        {
                            var status = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>((IntPtr)pBuffer);
                            return (MapStatusToString(status.dwCurrentState), (int)status.dwProcessId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"QueryStatus Failed: {ex.Message}");
            }
            return ("未知", 0);
        }

        /// <summary>
        /// Maps Windows service state code to localized string
        /// </summary>
        private static string MapStatusToString(uint state) => state switch
        {
            1 => "已停止",
            2 => "启动中",
            3 => "停止中",
            4 => "运行中",
            5 => "继续中",
            6 => "暂停中",
            7 => "已暂停",
            _ => "未知"
        };
    }
}
