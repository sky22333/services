using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Services.Core.Services
{
    public class ServiceMonitor : IDisposable
    {
        private const uint SERVICE_NOTIFY_STOPPED = 0x00000001;
        private const uint SERVICE_NOTIFY_START_PENDING = 0x00000002;
        private const uint SERVICE_NOTIFY_STOP_PENDING = 0x00000004;
        private const uint SERVICE_NOTIFY_RUNNING = 0x00000008;
        private const uint SERVICE_NOTIFY_CONTINUE_PENDING = 0x00000010;
        private const uint SERVICE_NOTIFY_PAUSE_PENDING = 0x00000020;
        private const uint SERVICE_NOTIFY_PAUSED = 0x00000040;
        private const uint SERVICE_NOTIFY_CREATED = 0x00000080;
        private const uint SERVICE_NOTIFY_DELETED = 0x00000100;
        private const uint SERVICE_NOTIFY_DELETE_PENDING = 0x00000200;

        private const uint SERVICE_NOTIFY_STATUS_CHANGE = 
            SERVICE_NOTIFY_STOPPED | SERVICE_NOTIFY_START_PENDING | SERVICE_NOTIFY_STOP_PENDING |
            SERVICE_NOTIFY_RUNNING | SERVICE_NOTIFY_CONTINUE_PENDING | SERVICE_NOTIFY_PAUSE_PENDING |
            SERVICE_NOTIFY_PAUSED;

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SERVICE_QUERY_STATUS = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_NOTIFY
        {
            public uint dwVersion;
            public IntPtr pfnNotifyCallback;
            public IntPtr pContext;
            public uint dwNotificationStatus;
            public SERVICE_STATUS_PROCESS ServiceStatus;
            public uint dwNotificationTriggered;
            public IntPtr pszServiceNames;
        }

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

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint NotifyServiceStatusChange(IntPtr hService, uint dwNotifyMask, IntPtr pNotifyBuffer);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr hService, int infoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

        private IntPtr _hSCManager;
        private IntPtr _hService;
        private IntPtr _pNotifyBuffer;
        private bool _isMonitoring;
        private readonly string _serviceName;
        private AutoResetEvent? _notifyEvent;
        private RegisteredWaitHandle? _registeredWait;
        
        public event EventHandler<ServiceStatusEventArgs>? StatusChanged;

        public class ServiceStatusEventArgs : EventArgs
        {
            public required string Status { get; set; }
            public int Pid { get; set; }
        }

        public (string Status, int Pid) GetCurrentStatus()
        {
            if (_hService == IntPtr.Zero) return ("未知", 0);

            try
            {
                // Use stackalloc to avoid heap allocation for small buffer
                Span<byte> buffer = stackalloc byte[1024];
                unsafe
                {
                    fixed (byte* pBuffer = buffer)
                    {
                        uint bytesNeeded;
                        if (QueryServiceStatusEx(_hService, 0, (IntPtr)pBuffer, 1024, out bytesNeeded))
                        {
                            var status = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>((IntPtr)pBuffer);
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
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetCurrentStatus Failed: {ex.Message}");
            }
            return ("未知", 0);
        }

        public ServiceMonitor(string serviceName)
        {
            _serviceName = serviceName;
        }

        public void StartMonitoring()
        {
            if (_isMonitoring) return;

            try
            {
                _hSCManager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (_hSCManager == IntPtr.Zero) return;

                _hService = OpenService(_hSCManager, _serviceName, SERVICE_QUERY_STATUS);
                if (_hService == IntPtr.Zero)
                {
                    CloseServiceHandle(_hSCManager);
                    _hSCManager = IntPtr.Zero;
                    return;
                }

                _pNotifyBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SERVICE_NOTIFY)));
                
                _notifyEvent = new AutoResetEvent(false);

                _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                    _notifyEvent, 
                    (state, timeout) => NotifyCallback(), 
                    null, 
                    -1,
                    false
                );

                _isMonitoring = true;
                RegisterNotification();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StartMonitoring Failed: {ex}");
                StopMonitoring();
            }
        }

        private void RegisterNotification()
        {
            if (!_isMonitoring || _hService == IntPtr.Zero || _pNotifyBuffer == IntPtr.Zero || _notifyEvent == null) return;

            var notifyStruct = new SERVICE_NOTIFY
            {
                dwVersion = 2,
                pfnNotifyCallback = IntPtr.Zero,
                pContext = _notifyEvent.SafeWaitHandle.DangerousGetHandle(),
                dwNotificationStatus = 0,
                dwNotificationTriggered = 0,
                pszServiceNames = IntPtr.Zero,
                ServiceStatus = new SERVICE_STATUS_PROCESS()
            };
            
            notifyStruct.pContext = _notifyEvent.SafeWaitHandle.DangerousGetHandle();

            Marshal.StructureToPtr(notifyStruct, _pNotifyBuffer, false);

            uint result = NotifyServiceStatusChange(_hService, SERVICE_NOTIFY_STATUS_CHANGE, _pNotifyBuffer);
            if (result != 0)
            {
                StopMonitoring();
            }
        }

        private void NotifyCallback()
        {
            if (!_isMonitoring) return;

            try
            {
                var (statusStr, pid) = GetCurrentStatus();

                StatusChanged?.Invoke(this, new ServiceStatusEventArgs { Status = statusStr, Pid = pid });

                RegisterNotification();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"NotifyCallback Error: {ex}");
            }
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            
            _registeredWait?.Unregister(null);
            _registeredWait = null;

            _notifyEvent?.Close();
            _notifyEvent = null;
            
            if (_hService != IntPtr.Zero)
            {
                CloseServiceHandle(_hService);
                _hService = IntPtr.Zero;
            }
            
            if (_hSCManager != IntPtr.Zero)
            {
                CloseServiceHandle(_hSCManager);
                _hSCManager = IntPtr.Zero;
            }

            if (_pNotifyBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pNotifyBuffer);
                _pNotifyBuffer = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            StopMonitoring();
        }
    }
}
