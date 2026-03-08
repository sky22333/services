using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading.Tasks;
using Microsoft.Win32;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Core.Services
{
    public class WindowsServiceManager : IDisposable
    {
        private Dictionary<string, Service> _services = new();
        private readonly Dictionary<string, ServiceMonitor> _monitors = new();
        private readonly Dictionary<ServiceMonitor, string> _monitorToServiceId = new();
        public event EventHandler<Service>? ServiceUpdated;
        private readonly object _lock = new();

        public WindowsServiceManager()
        {
        }

        public async Task InitializeAsync()
        {
            // Reload services to reflect changes
            await LoadServicesAsync();
        }

        public Task<List<Service>> GetServicesSnapshotAsync()
        {
            lock (_lock)
            {
                // Ensure monitors exist (lazy initialization)
                foreach (var service in _services.Values)
                {
                    if (!_monitors.ContainsKey(service.Id))
                    {
                        var monitor = new ServiceMonitor(service.Id);
                        monitor.StatusChanged += OnMonitorStatusChanged;
                        monitor.StartMonitoring();
                        _monitors[service.Id] = monitor;
                        _monitorToServiceId[monitor] = service.Id; // Add reverse mapping
                    }
                }

                return Task.FromResult(_services.Values.Select(CloneService).ToList());
            }
        }

        private void OnMonitorStatusChanged(object? sender, ServiceMonitor.ServiceStatusEventArgs e)
        {
            Service? clonedService = null;

            lock (_lock)
            {
                // O(1) lookup using reverse mapping instead of O(n) iteration
                if (sender is ServiceMonitor monitor && 
                    _monitorToServiceId.TryGetValue(monitor, out var serviceId) &&
                    _services.TryGetValue(serviceId, out var trackedService))
                {
                    // Check if status actually changed to avoid unnecessary UI updates
                    if (trackedService.Status != e.Status || trackedService.Pid != e.Pid)
                    {
                        trackedService.Status = e.Status;
                        trackedService.Pid = e.Pid;
                        trackedService.UpdatedAt = DateTime.Now;
                        clonedService = CloneService(trackedService);
                        
                        System.Diagnostics.Debug.WriteLine($"[WindowsServiceManager] Service '{serviceId}' status updated: {e.Status} (PID: {e.Pid})");
                    }
                }
            }

            // Invoke event outside of lock to prevent deadlock
            if (clonedService != null)
            {
                ServiceUpdated?.Invoke(this, clonedService);
            }
        }

        public void Dispose()
        {
            foreach (var monitor in _monitors.Values)
            {
                monitor.Dispose();
            }
            _monitors.Clear();
            _monitorToServiceId.Clear();
            GC.SuppressFinalize(this);
        }

        private static Service CloneService(Service s)
        {
            return new Service
            {
                Id = s.Id,
                Name = s.Name,
                Status = s.Status,
                Pid = s.Pid,
                ExePath = s.ExePath,
                Args = s.Args,
                WorkingDir = s.WorkingDir,
                AutoRestart = s.AutoRestart,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt
            };
        }

        private async Task UpdateServiceStatusAsync(Service service)
        {
            if (service == null) return;
            
            await Task.Run(() =>
            {
                var (status, pid) = ServiceUtils.GetServiceStatus(service.Id);
                
                // Only trigger update if status changed
                if (service.Status != status || service.Pid != pid)
                {
                    service.Status = status;
                    service.Pid = pid;
                    service.UpdatedAt = DateTime.Now;
                    
                    System.Diagnostics.Debug.WriteLine($"[WindowsServiceManager] Manual status update for '{service.Id}': {status} (PID: {pid})");
                    
                    ServiceUpdated?.Invoke(this, CloneService(service));
                }
                else
                {
                    service.UpdatedAt = DateTime.Now;
                }
            });
        }

        public async Task CreateServiceAsync(ServiceConfig config)
        {
            if (!File.Exists(config.ExePath))
                throw new FileNotFoundException("Executable not found", config.ExePath);

            // Security Validation
            if (config.Name.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != ' '))
                throw new ArgumentException("Service Name contains invalid characters.");

            if (config.Name.Contains("\"") || config.Name.Contains("\n") || config.Name.Contains("\r"))
                throw new ArgumentException("Service Name contains illegal characters.");

            string serviceName = GenerateServiceName(config.Name);

            // Double check registry instead of local cache
            using (var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}"))
            {
                if (key != null) throw new Exception($"Service {serviceName} already exists");
            }

            var module = Process.GetCurrentProcess().MainModule;
            if (module == null) throw new Exception("Cannot determine current executable path");
            string currentExe = module.FileName;

            // Construct command safely
            string wrapperCmd = $"\"{currentExe}\" --service-wrapper \"{serviceName}\"";

            // Use P/Invoke to create service
            IntPtr scmHandle = ServiceUtils.OpenSCManager(null, null, ServiceUtils.SC_MANAGER_CREATE_SERVICE);
            if (scmHandle == IntPtr.Zero)
                throw new Exception($"Failed to open SC Manager. Error: {Marshal.GetLastWin32Error()}");

            try
            {
                IntPtr serviceHandle = ServiceUtils.CreateService(
                    scmHandle,
                    serviceName,
                    config.Name,
                    ServiceUtils.SERVICE_ALL_ACCESS,
                    ServiceUtils.SERVICE_WIN32_OWN_PROCESS,
                    (uint)config.StartupType,
                    ServiceUtils.SERVICE_ERROR_NORMAL,
                    wrapperCmd,
                    null,
                    IntPtr.Zero,
                    null,
                    null,
                    null);

                if (serviceHandle == IntPtr.Zero)
                    throw new Exception($"Failed to create service. Error: {Marshal.GetLastWin32Error()}");

                ServiceUtils.CloseServiceHandle(serviceHandle);
            }
            finally
            {
                ServiceUtils.CloseServiceHandle(scmHandle);
            }

            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using (var servicesKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", true))
                {
                    if (servicesKey != null)
                    {
                        using (var serviceKey = servicesKey.OpenSubKey(serviceName, true))
                        {
                            if (serviceKey != null)
                            {
                                using (var paramsKey = serviceKey.CreateSubKey("Parameters"))
                                {
                                    paramsKey.SetValue("ExePath", config.ExePath);
                                    paramsKey.SetValue("Args", config.Args ?? "");
                                    paramsKey.SetValue("WorkingDir", string.IsNullOrEmpty(config.WorkingDir) ? Path.GetDirectoryName(config.ExePath) ?? "" : config.WorkingDir);
                                    paramsKey.SetValue("DisplayName", config.Name);
                                    paramsKey.SetValue("AutoRestart", config.AutoRestart ? 1 : 0);
                                    paramsKey.SetValue("CreatedAt", DateTime.Now.ToString("o"));
                                    paramsKey.SetValue("ManagedBy", "WindowsServiceManager");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await DeleteServiceAsync(serviceName);
                throw new Exception($"Failed to configure service registry: {ex.Message}");
            }

            await RunCommandAsync("sc.exe", $"description \"{serviceName}\" \"Managed by Windows Service Manager: {config.Name}\"");

            // Configure recovery actions: Restart service after 1 minute if it fails (e.g. dependencies not ready)
            await RunCommandAsync("sc.exe", $"failure \"{serviceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000");

            await LoadServicesAsync();
        }

        private async Task RunCommandAsync(string command, string args)
        {
            var psi = new ProcessStartInfo(command, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p != null)
            {
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                {
                    var err = await p.StandardError.ReadToEndAsync();
                    throw new Exception($"Command failed: {command} {args}\nError: {err}");
                }
            }
        }

        private string GenerateServiceName(string displayName)
        {
            var safe = new string(displayName.Where(c => char.IsLetterOrDigit(c)).ToArray());
            return $"WinSvcMgr_{safe}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        }

        public async Task StartServiceAsync(string serviceId)
        {
            Service? service;
            lock (_lock)
            {
                if (!_services.TryGetValue(serviceId, out service)) throw new Exception("Service not found");
            }

            using var sc = new ServiceController(serviceId);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                try
                {
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
                catch (System.ServiceProcess.TimeoutException) { }
            }
            
            // UpdateServiceStatusAsync already triggers ServiceUpdated event
            await UpdateServiceStatusAsync(service);
        }

        public async Task StopServiceAsync(string serviceId)
        {
            Service? service;
            lock (_lock)
            {
                if (!_services.TryGetValue(serviceId, out service)) throw new Exception("Service not found");
            }

            using var sc = new ServiceController(serviceId);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                try
                {
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                catch (System.ServiceProcess.TimeoutException) { }
            }
            
            // UpdateServiceStatusAsync already triggers ServiceUpdated event
            await UpdateServiceStatusAsync(service);
        }

        public async Task DeleteServiceAsync(string serviceId)
        {
            lock (_lock)
            {
                if (!_services.ContainsKey(serviceId)) throw new Exception("Service not found");
            }

            // Stop service first
            await StopServiceAsync(serviceId);

            // Then dispose and remove monitor to avoid callback issues
            if (_monitors.TryGetValue(serviceId, out var monitor))
            {
                monitor.Dispose();
                _monitors.Remove(serviceId);
                _monitorToServiceId.Remove(monitor); // Remove reverse mapping
            }

            // Use P/Invoke to delete service
            IntPtr scmHandle = ServiceUtils.OpenSCManager(null, null, ServiceUtils.SC_MANAGER_CONNECT);
            if (scmHandle == IntPtr.Zero)
                throw new Exception($"Failed to open SC Manager. Error: {Marshal.GetLastWin32Error()}");

            try
            {
                // We need DELETE access
                IntPtr serviceHandle = ServiceUtils.OpenService(scmHandle, serviceId, ServiceUtils.DELETE);
                if (serviceHandle == IntPtr.Zero)
                    throw new Exception($"Failed to open service for deletion. Error: {Marshal.GetLastWin32Error()}");

                try
                {
                    if (!ServiceUtils.DeleteService(serviceHandle))
                        throw new Exception($"Failed to delete service. Error: {Marshal.GetLastWin32Error()}");
                }
                finally
                {
                    ServiceUtils.CloseServiceHandle(serviceHandle);
                }
            }
            finally
            {
                ServiceUtils.CloseServiceHandle(scmHandle);
            }

            lock (_lock)
            {
                _services.Remove(serviceId);
            }
        }

        private async Task LoadServicesAsync()
        {
            var services = new Dictionary<string, Service>();
            await Task.Run(() =>
            {
                try
                {
                    // FORCE 64-bit registry view to avoid redirection on 64-bit OS
                    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var servicesKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");

                    if (servicesKey != null)
                    {
                        foreach (var serviceName in servicesKey.GetSubKeyNames())
                        {
                            try
                            {
                                using (var serviceKey = servicesKey.OpenSubKey(serviceName))
                                {
                                    if (serviceKey != null)
                                    {
                                        using (var paramsKey = serviceKey.OpenSubKey("Parameters"))
                                        {
                                            if (paramsKey != null)
                                            {
                                                // Check if it's managed by us or has our signature parameters
                                                var exePath = paramsKey.GetValue("ExePath") as string;
                                                if (!string.IsNullOrEmpty(exePath))
                                                {
                                                    var displayName = paramsKey.GetValue("DisplayName") as string ?? serviceName;
                                                    var args = paramsKey.GetValue("Args") as string;
                                                    var workingDir = paramsKey.GetValue("WorkingDir") as string;
                                                    var autoRestartVal = paramsKey.GetValue("AutoRestart");
                                                    bool autoRestart = (autoRestartVal is int val && val == 1);

                                                    var createdAtStr = paramsKey.GetValue("CreatedAt") as string;
                                                    DateTime createdAt = DateTime.Now;
                                                    if (DateTime.TryParse(createdAtStr, out var dt)) createdAt = dt;

                                                    // Optimized: Get status immediately during initialization to prevent "Unknown" flicker
                                                    var (status, pid) = ServiceUtils.GetServiceStatus(serviceName);

                                                    var service = new Service
                                                    {
                                                        Id = serviceName,
                                                        Name = displayName,
                                                        ExePath = exePath,
                                                        Args = args,
                                                        WorkingDir = workingDir,
                                                        AutoRestart = autoRestart,
                                                        CreatedAt = createdAt,
                                                        UpdatedAt = DateTime.Now,
                                                        Status = status,
                                                        Pid = pid
                                                    };
                                                    services[serviceName] = service;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            });

            lock (_lock)
            {
                _services = services;
            }
        }
    }
}
