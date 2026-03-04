using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Core.Services
{
    public class WindowsServiceManager : IDisposable
    {
        private const string DataFile = "windows_services_data.json";
        private readonly string _dataFilePath;
        private Dictionary<string, Service> _services = new();
        private readonly Dictionary<string, ServiceMonitor> _monitors = new();

        public event EventHandler<Service>? ServiceUpdated;

        private readonly object _lock = new();

        public WindowsServiceManager()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dataDir = Path.Combine(appData, "ServicesManager");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }
            _dataFilePath = Path.Combine(dataDir, DataFile);
        }

        public async Task InitializeAsync()
        {
            await LoadServicesAsync();
        }

        public async Task<List<Service>> GetServicesAsync()
        {
            // Do not reload from disk every time. Only memory state is returned.
            // Disk sync happens on modification.

            List<Service> currentServices;
            lock (_lock)
            {
                currentServices = _services.Values.ToList();
            }

            // Concurrently update status for all services
            // This allows the UI to get fresh status without waiting for the monitor interval
            var tasks = currentServices.Select(UpdateServiceStatusAsync);
            await Task.WhenAll(tasks);

            lock (_lock)
            {
                foreach (var service in currentServices)
                {
                    if (!_monitors.ContainsKey(service.Id))
                    {
                        var monitor = new ServiceMonitor(service.Id);
                        monitor.StatusChanged += (s, e) =>
                        {
                            lock (_lock)
                            {
                                if (_services.TryGetValue(service.Id, out var trackedService))
                                {
                                    trackedService.Status = e.Status;
                                    trackedService.Pid = e.Pid;
                                    trackedService.UpdatedAt = DateTime.Now;
                                    ServiceUpdated?.Invoke(this, CloneService(trackedService));
                                }
                            }
                        };
                        monitor.StartMonitoring();
                        _monitors[service.Id] = monitor;
                    }
                }
                return _services.Values.Select(CloneService).ToList();
            }
        }

        public void Dispose()
        {
            foreach (var monitor in _monitors.Values)
            {
                monitor.Dispose();
            }
            _monitors.Clear();
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
                AutoStart = s.AutoStart,
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
                service.Status = status;
                service.Pid = pid;
                service.UpdatedAt = DateTime.Now;
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

            lock (_lock)
            {
                if (_services.ContainsKey(serviceName))
                    throw new Exception($"Service {serviceName} already exists");
            }

            var module = Process.GetCurrentProcess().MainModule;
            if (module == null) throw new Exception("Cannot determine current executable path");
            string currentExe = module.FileName;

            // Construct command safely
            string wrapperCmd = $"\\\"{currentExe}\\\" --service-wrapper \\\"{serviceName}\\\"";

            // We still use string interpolation for sc.exe because of its specific syntax requirements,
            // but we have validated the input Name.
            await RunCommandAsync("sc.exe", $"create \"{serviceName}\" binPath= \"{wrapperCmd}\" start= auto DisplayName= \"{config.Name}\"");

            try
            {
                using (var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", true))
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
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await RunCommandAsync("sc.exe", $"delete \"{serviceName}\"");
                throw new Exception($"Failed to configure service registry: {ex.Message}");
            }

            await RunCommandAsync("sc.exe", $"description \"{serviceName}\" \"Managed by Windows Service Manager: {config.Name}\"");

            var service = new Service
            {
                Id = serviceName,
                Name = config.Name,
                ExePath = config.ExePath,
                Args = config.Args,
                WorkingDir = config.WorkingDir,
                Status = "已停止",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                AutoStart = true
            };

            lock (_lock)
            {
                _services[serviceName] = service;
                SaveServices();
            }
        }

        public Task UpdateServiceAsync(string serviceId, ServiceConfig config)
        {
            Service? existingService;
            lock (_lock)
            {
                if (!_services.TryGetValue(serviceId, out existingService))
                {
                    throw new KeyNotFoundException($"Service {serviceId} not found");
                }
            }

            if (!File.Exists(config.ExePath))
            {
                throw new FileNotFoundException("Executable not found", config.ExePath);
            }

            // Update registry configuration
            try
            {
                using (var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", true))
                {
                    if (servicesKey != null)
                    {
                        using (var serviceKey = servicesKey.OpenSubKey(serviceId, true))
                        {
                            if (serviceKey != null)
                            {
                                using (var paramsKey = serviceKey.CreateSubKey("Parameters"))
                                {
                                    paramsKey.SetValue("ExePath", config.ExePath);
                                    paramsKey.SetValue("Args", config.Args ?? "");
                                    paramsKey.SetValue("WorkingDir", string.IsNullOrEmpty(config.WorkingDir) ? Path.GetDirectoryName(config.ExePath) ?? "" : config.WorkingDir);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to update service configuration: {ex.Message}");
            }

            // Update local model
            lock (_lock)
            {
                existingService.ExePath = config.ExePath;
                existingService.Args = config.Args;
                existingService.WorkingDir = config.WorkingDir;
                existingService.UpdatedAt = DateTime.Now;

                SaveServices();
                ServiceUpdated?.Invoke(this, CloneService(existingService));
            }

            return Task.CompletedTask;
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
            }

            if (_monitors.TryGetValue(serviceId, out var monitor))
            {
                monitor.Dispose();
                _monitors.Remove(serviceId);
            }

            await StopServiceAsync(serviceId);
            await RunCommandAsync("sc.exe", $"delete {serviceId}");

            lock (_lock)
            {
                _services.Remove(serviceId);
                SaveServices();
            }
        }

        private async Task LoadServicesAsync()
        {
            if (File.Exists(_dataFilePath))
            {
                try
                {
                    string json;
                    using (var reader = new StreamReader(_dataFilePath))
                    {
                        json = await reader.ReadToEndAsync();
                    }

                    var list = JsonSerializer.Deserialize<Dictionary<string, Service>>(json);
                    if (list != null)
                    {
                        lock (_lock)
                        {
                            _services = list;
                        }
                    }
                }
                catch { }
            }
        }

        private void SaveServices()
        {
            var json = JsonSerializer.Serialize(_services, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_dataFilePath, json);
        }
    }
}
