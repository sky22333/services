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
        public event EventHandler<Service>? ServiceUpdated;
        private readonly object _lock = new();

        public WindowsServiceManager()
        {
        }

        public async Task InitializeAsync()
        {
            await LoadServicesAsync();
            CleanupOrphanedMonitors();
        }

        public async Task<List<Service>> GetServicesAsync()
        {
            return await GetServicesSnapshotAsync();
        }

        public async Task RefreshServiceStatusesAsync()
        {
            List<Service> servicesToUpdate;
            lock (_lock)
            {
                servicesToUpdate = _services.Values.ToList();
            }

            if (servicesToUpdate.Count == 0) return;

            var tasks = servicesToUpdate.Select(UpdateServiceStatusAsync);
            await Task.WhenAll(tasks);
        }

        public Task<List<Service>> GetServicesSnapshotAsync()
        {
            lock (_lock)
            {
                foreach (var service in _services.Values)
                {
                    if (!_monitors.ContainsKey(service.Id))
                    {
                        var serviceId = service.Id;
                        var monitor = new ServiceMonitor(serviceId);
                        monitor.StatusChanged += (s, e) =>
                        {
                            lock (_lock)
                            {
                                if (_services.TryGetValue(serviceId, out var trackedService))
                                {
                                    if (trackedService.Status != e.Status || trackedService.Pid != e.Pid)
                                    {
                                        trackedService.Status = e.Status;
                                        trackedService.Pid = e.Pid;
                                        trackedService.UpdatedAt = DateTime.Now;
                                        ServiceUpdated?.Invoke(this, CloneService(trackedService));
                                    }
                                }
                            }
                        };
                        monitor.StartMonitoring();
                        _monitors[serviceId] = monitor;
                    }
                }

                return Task.FromResult(_services.Values.Select(CloneService).ToList());
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var monitor in _monitors.Values)
                {
                    monitor.Dispose();
                }
                _monitors.Clear();
                _services.Clear();
            }
            GC.SuppressFinalize(this);
        }

        private void CleanupOrphanedMonitors()
        {
            lock (_lock)
            {
                var orphanedKeys = _monitors.Keys.Except(_services.Keys).ToList();
                foreach (var key in orphanedKeys)
                {
                    if (_monitors.TryGetValue(key, out var monitor))
                    {
                        monitor.Dispose();
                        _monitors.Remove(key);
                    }
                }
            }
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
                AutoStart = s.AutoStart,
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
                
                if (service.Status != status || service.Pid != pid)
                {
                    service.Status = status;
                    service.Pid = pid;
                    service.UpdatedAt = DateTime.Now;
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

                        // Add to managed services index for fast loading
                        AddToManagedServicesIndex(serviceName);
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
            await UpdateServiceStatusAsync(service);
            ServiceUpdated?.Invoke(this, service);
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
            await UpdateServiceStatusAsync(service);
            ServiceUpdated?.Invoke(this, service);
        }

        public async Task DeleteServiceAsync(string serviceId)
        {
            lock (_lock)
            {
                if (!_services.ContainsKey(serviceId)) throw new Exception("Service not found");
                
                // 清理 monitor
                if (_monitors.TryGetValue(serviceId, out var monitor))
                {
                    monitor.Dispose();
                    _monitors.Remove(serviceId);
                }
            }

            await StopServiceAsync(serviceId);

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
            
            // Remove from managed services index
            RemoveFromManagedServicesIndex(serviceId);

            lock (_lock)
            {
                _services.Remove(serviceId);
                
                // 再次确保 monitor 已清理
                if (_monitors.ContainsKey(serviceId))
                {
                    _monitors.Remove(serviceId);
                }
            }
        }

        private void AddToManagedServicesIndex(string serviceName)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var indexKey = hklm.CreateSubKey(@"SOFTWARE\WindowsServiceManager\ManagedServices");
                using var serviceIndexKey = indexKey.CreateSubKey(serviceName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to add service to index: {ex.Message}");
            }
        }

        private void RemoveFromManagedServicesIndex(string serviceName)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var indexKey = hklm.OpenSubKey(@"SOFTWARE\WindowsServiceManager\ManagedServices", true);
                indexKey?.DeleteSubKey(serviceName, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to remove service from index: {ex.Message}");
            }
        }

        private async Task LoadServicesAsync()
        {
            var services = new Dictionary<string, Service>();
            await Task.Run(() =>
            {
                try
                {
                    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var indexKey = hklm.OpenSubKey(@"SOFTWARE\WindowsServiceManager\ManagedServices");
                    
                    if (indexKey != null)
                    {
                        var managedServiceNames = indexKey.GetSubKeyNames();
                        using var servicesKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                        if (servicesKey != null)
                        {
                            foreach (var serviceName in managedServiceNames)
                            {
                                try
                                {
                                    LoadSingleService(servicesKey, serviceName, services);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Failed to load service {serviceName}: {ex.Message}");
                                }
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Index not found, performing full scan.");
                        LoadServicesLegacy(hklm, services);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"LoadServicesAsync error: {ex.Message}");
                }
            });

            lock (_lock)
            {
                var removedServiceIds = _services.Keys.Except(services.Keys).ToList();
                foreach (var serviceId in removedServiceIds)
                {
                    if (_monitors.TryGetValue(serviceId, out var monitor))
                    {
                        monitor.Dispose();
                        _monitors.Remove(serviceId);
                    }
                }
                
                _services = services;
            }
        }

        private void LoadSingleService(RegistryKey servicesKey, string serviceName, Dictionary<string, Service> services)
        {
            using var serviceKey = servicesKey.OpenSubKey(serviceName);
            if (serviceKey == null)
            {
                System.Diagnostics.Debug.WriteLine($"Service {serviceName} not found, may have been deleted.");
                return;
            }

            using var paramsKey = serviceKey.OpenSubKey("Parameters");
            if (paramsKey == null) return;

            var exePath = paramsKey.GetValue("ExePath") as string;
            if (string.IsNullOrEmpty(exePath)) return;

            var displayName = paramsKey.GetValue("DisplayName") as string ?? serviceName;
            var args = paramsKey.GetValue("Args") as string;
            var workingDir = paramsKey.GetValue("WorkingDir") as string;
            var autoRestartVal = paramsKey.GetValue("AutoRestart");
            bool autoRestart = (autoRestartVal is int val && val == 1);

            var createdAtStr = paramsKey.GetValue("CreatedAt") as string;
            DateTime createdAt = DateTime.Now;
            if (DateTime.TryParse(createdAtStr, out var dt)) createdAt = dt;

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
                AutoStart = true,
                Status = status,
                Pid = pid
            };
            services[serviceName] = service;
        }

        private void LoadServicesLegacy(RegistryKey hklm, Dictionary<string, Service> services)
        {
            using var servicesKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey == null) return;

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var serviceKey = servicesKey.OpenSubKey(serviceName);
                    if (serviceKey == null) continue;

                    using var paramsKey = serviceKey.OpenSubKey("Parameters");
                    if (paramsKey == null) continue;

                    var exePath = paramsKey.GetValue("ExePath") as string;
                    if (string.IsNullOrEmpty(exePath)) continue;

                    LoadSingleService(servicesKey, serviceName, services);
                    AddToManagedServicesIndex(serviceName);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load service {serviceName}: {ex.Message}");
                }
            }
        }
    }
}
