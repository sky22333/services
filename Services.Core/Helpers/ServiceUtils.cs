using System;
using System.Runtime.InteropServices;

namespace Services.Core.Helpers
{
    public static class ServiceUtils
    {
        public const uint SERVICE_QUERY_STATUS = 0x0004;
        public const uint SC_MANAGER_CONNECT = 0x0001;
        public const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
        public const uint SERVICE_ALL_ACCESS = 0xF01FF;
        public const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
        public const uint SERVICE_AUTO_START = 0x00000002;
        public const uint SERVICE_ERROR_NORMAL = 0x00000001;
        public const uint DELETE = 0x00010000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS_PROCESS
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

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateService(
            IntPtr hSCManager,
            string lpServiceName,
            string lpDisplayName,
            uint dwDesiredAccess,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string? lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string? lpDependencies,
            string? lpServiceStartName,
            string? lpPassword);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool QueryServiceStatusEx(IntPtr hService, int infoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

        public static (string Status, int Pid) GetServiceStatus(string serviceName)
        {
            IntPtr hSCManager = IntPtr.Zero;
            IntPtr hService = IntPtr.Zero;
            IntPtr ptr = IntPtr.Zero;

            try
            {
                hSCManager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (hSCManager == IntPtr.Zero) return ("未知", 0);

                hService = OpenService(hSCManager, serviceName, SERVICE_QUERY_STATUS);
                if (hService == IntPtr.Zero) return ("未知", 0);

                uint bytesNeeded;
                ptr = Marshal.AllocHGlobal(1024);

                if (QueryServiceStatusEx(hService, 0, ptr, 1024, out bytesNeeded))
                {
                    var status = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(ptr);
                    string statusStr = status.dwCurrentState switch
                    {
                        1 => "已停止",
                        2 => "启动中",
                        3 => "停止中",
                        4 => "运行中",
                        _ => "未知"
                    };
                    return (statusStr, (int)status.dwProcessId);
                }
            }
            catch
            {
                // Ignore exceptions
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
                if (hService != IntPtr.Zero) CloseServiceHandle(hService);
                if (hSCManager != IntPtr.Zero) CloseServiceHandle(hSCManager);
            }

            return ("未知", 0);
        }
    }
}
