using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Services.Core.Helpers;

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

        private const uint SERVICE_NOTIFY_STATUS_CHANGE =
            SERVICE_NOTIFY_STOPPED | SERVICE_NOTIFY_START_PENDING | SERVICE_NOTIFY_STOP_PENDING |
            SERVICE_NOTIFY_RUNNING | SERVICE_NOTIFY_CONTINUE_PENDING | SERVICE_NOTIFY_PAUSE_PENDING |
            SERVICE_NOTIFY_PAUSED;

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const uint SERVICE_QUERY_CONFIG = 0x0001;
        
        // Combined access rights required for NotifyServiceStatusChange
        private const uint SERVICE_NOTIFY_ACCESS = SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_NOTIFY_2
        {
            public uint dwVersion;
            public IntPtr pfnNotifyCallback;
            public IntPtr pContext;
            public uint dwNotificationStatus;
            public SERVICE_STATUS_PROCESS ServiceStatus;
            public uint dwNotificationTriggered;
            public IntPtr pszServiceNames;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint NotifyServiceStatusChange(IntPtr hService, uint dwNotifyMask, IntPtr pNotifyBuffer);

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
            return ServiceStatusHelper.QueryStatus(_hService);
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
                if (_hSCManager == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] OpenSCManager failed for '{_serviceName}': Error {error}");
                    return;
                }

                // CRITICAL FIX: Use SERVICE_NOTIFY_ACCESS (includes SERVICE_QUERY_CONFIG)
                // This is required for NotifyServiceStatusChange to work properly
                _hService = OpenService(_hSCManager, _serviceName, SERVICE_NOTIFY_ACCESS);
                if (_hService == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] OpenService failed for '{_serviceName}': Error {error}");
                    CloseServiceHandle(_hSCManager);
                    _hSCManager = IntPtr.Zero;
                    return;
                }

                _pNotifyBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SERVICE_NOTIFY_2)));

                _notifyEvent = new AutoResetEvent(false);

                _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                    _notifyEvent,
                    (state, timeout) => NotifyCallback(),
                    null,
                    -1,
                    false
                );

                _isMonitoring = true;
                
                // Register for notifications
                if (!RegisterNotification())
                {
                    System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] Initial notification registration failed for '{_serviceName}'");
                    StopMonitoring();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] Successfully started monitoring '{_serviceName}'");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] StartMonitoring exception for '{_serviceName}': {ex.Message}");
                StopMonitoring();
            }
        }

        private bool RegisterNotification()
        {
            if (!_isMonitoring || _hService == IntPtr.Zero || _pNotifyBuffer == IntPtr.Zero || _notifyEvent == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] RegisterNotification precondition failed for '{_serviceName}'");
                return false;
            }

            try
            {
                var notifyStruct = new SERVICE_NOTIFY_2
                {
                    dwVersion = 2,
                    pfnNotifyCallback = IntPtr.Zero,
                    pContext = _notifyEvent.SafeWaitHandle.DangerousGetHandle(),
                    dwNotificationStatus = 0,
                    dwNotificationTriggered = 0,
                    pszServiceNames = IntPtr.Zero,
                    ServiceStatus = new SERVICE_STATUS_PROCESS()
                };

                Marshal.StructureToPtr(notifyStruct, _pNotifyBuffer, true);

                uint result = NotifyServiceStatusChange(_hService, SERVICE_NOTIFY_STATUS_CHANGE, _pNotifyBuffer);
                
                if (result != 0)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    string errorMessage = errorCode switch
                    {
                        5 => "ERROR_ACCESS_DENIED - Insufficient permissions (need SERVICE_QUERY_CONFIG)",
                        87 => "ERROR_INVALID_PARAMETER - Invalid parameter",
                        1062 => "ERROR_SERVICE_NOT_ACTIVE - Service is not running",
                        1072 => "ERROR_SERVICE_MARKED_FOR_DELETE - Service is marked for deletion",
                        _ => $"Unknown error code: {errorCode}"
                    };
                    
                    System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] NotifyServiceStatusChange failed for '{_serviceName}': {errorMessage}");
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] RegisterNotification exception for '{_serviceName}': {ex.Message}");
                return false;
            }
        }

        private void NotifyCallback()
        {
            if (!_isMonitoring) return;

            try
            {
                var (statusStr, pid) = GetCurrentStatus();

                System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] Status changed for '{_serviceName}': {statusStr} (PID: {pid})");

                StatusChanged?.Invoke(this, new ServiceStatusEventArgs { Status = statusStr, Pid = pid });

                // Re-register for next notification
                if (!RegisterNotification())
                {
                    System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] Failed to re-register notification for '{_serviceName}', stopping monitor");
                    StopMonitoring();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] NotifyCallback error for '{_serviceName}': {ex.Message}");
                // Don't stop monitoring on callback errors, just log them
            }
        }

        public void StopMonitoring()
        {
            if (!_isMonitoring) return;
            
            _isMonitoring = false;

            System.Diagnostics.Debug.WriteLine($"[ServiceMonitor] Stopping monitoring for '{_serviceName}'");

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
