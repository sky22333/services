using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI;
using Services.Core.Services;
using Services.Core.Models;
using System.Collections.ObjectModel;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;

namespace Services.App
{
    public sealed partial class MainWindow : Window
    {
        private System.Windows.Forms.NotifyIcon? _notifyIcon;

        private readonly WindowsServiceManager _serviceManager;
        private readonly EnvironmentManager _envManager;
        private readonly LogManager _logManager;
        private AppWindow _appWindow;
        private bool _isRealExit = false;

        public ObservableCollection<Service> Services { get; } = new();

        public MainWindow()
        {
            this.InitializeComponent();
            ((FrameworkElement)this.Content).DataContext = this;
            
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            InitializeTrayIcon();

            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Closing += OnAppWindowClosing;
            _appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 750));

            _serviceManager = new WindowsServiceManager();
            _serviceManager.ServiceUpdated += OnServiceUpdated;

            _envManager = new EnvironmentManager();
            _logManager = new LogManager();

            LoadServices();
            Title = "Windows 服务管理器";
            
            this.Closed += (s, e) => _serviceManager.Dispose();
        }

        private void InitializeTrayIcon()
        {
            try
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon();
                _notifyIcon.Text = "Windows 服务管理器";
                
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
                }
                else
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                }

                _notifyIcon.Visible = true;
                _notifyIcon.DoubleClick += (s, e) => OnTrayOpenClick(null, null);

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                var showItem = new System.Windows.Forms.ToolStripMenuItem("显示窗口");
                showItem.Click += (s, e) => OnTrayOpenClick(null, null);
                
                var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
                exitItem.Click += (s, e) => 
                {
                    _isRealExit = true;
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    Application.Current.Exit();
                };

                contextMenu.Items.Add(showItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(exitItem);

                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tray Icon Init Failed: {ex}");
            }
        }

        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (!_isRealExit)
            {
                args.Cancel = true;
                _appWindow.Hide();
            }
        }

        private void OnTrayOpenClick(object? sender, object? e)
        {
            _appWindow.Show();
            this.Activate();
        }

        private void OnTrayExitClick(object sender, RoutedEventArgs e)
        {
            _isRealExit = true;
            _notifyIcon?.Dispose();
            
            Application.Current.Exit();
        }

        private void OnServiceUpdated(object? sender, Service service)
        {
            this.DispatcherQueue.TryEnqueue(() => 
            {
                var existing = Services.FirstOrDefault(s => s.Id == service.Id);
                if (existing != null)
                {
                    existing.Status = service.Status;
                    existing.Pid = service.Pid;
                    existing.UpdatedAt = service.UpdatedAt;
                }
            });
        }

        private async void LoadServices(bool silent = false)
        {
            try
            {
                if (!silent) UpdateStatus("正在加载服务...");
                var list = await _serviceManager.GetServicesAsync();
                
                for (int i = Services.Count - 1; i >= 0; i--)
                {
                    if (!list.Any(s => s.Id == Services[i].Id))
                    {
                        Services.RemoveAt(i);
                    }
                }
                
                foreach (var item in list)
                {
                    var existing = Services.FirstOrDefault(s => s.Id == item.Id);
                    if (existing == null)
                    {
                        Services.Add(item);
                    }
                    else
                    {
                        existing.Status = item.Status;
                        existing.Pid = item.Pid;
                    }
                }

                if (!silent) UpdateStatus($"已加载 {list.Count} 个服务。");
            }
            catch (Exception ex)
            {
                if (!silent) UpdateStatus($"加载服务失败: {ex.Message}");
            }
        }
        
        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            LoadServices();
        }

        private void UpdateStatus(string message)
        {
            if (StatusText != null) StatusText.Text = $"{DateTime.Now:HH:mm:ss} - {message}";
        }

        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                try
                {
                    UpdateStatus($"正在启动服务 {id}...");
                    await _serviceManager.StartServiceAsync(id);
                    UpdateStatus($"服务 {id} 已启动。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"启动服务失败: {ex.Message}");
                }
            }
        }

        private async void OnStopClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                try
                {
                    UpdateStatus($"正在停止服务 {id}...");
                    await _serviceManager.StopServiceAsync(id);
                    UpdateStatus($"服务 {id} 已停止。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"停止服务失败: {ex.Message}");
                }
            }
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var confirm = await ShowConfirmDialog("删除服务", $"确定要删除服务 '{id}' 吗？此操作无法撤销。");
                if (confirm)
                {
                    try
                    {
                        UpdateStatus($"正在删除服务 {id}...");
                        await _serviceManager.DeleteServiceAsync(id);
                        LoadServices(true);
                        UpdateStatus($"服务 {id} 已删除。");
                    }
                    catch (Exception ex)
                    {
                        await ShowDialog("错误", $"删除服务失败: {ex.Message}");
                    }
                }
            }
        }

        private async void OnLogsClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string serviceName)
            {
                var service = Services.FirstOrDefault(s => s.Name == serviceName);
                if (service != null)
                {
                    await ShowLogViewer(service.Id, service.Name);
                }
                else
                {
                    await ShowLogViewer(serviceName, serviceName);
                }
            }
        }

        private async Task ShowLogViewer(string serviceId, string displayName)
        {
            var dialog = new ContentDialog
            {
                Title = $"日志 - {displayName}",
                CloseButtonText = "关闭",
                XamlRoot = this.Content.XamlRoot,
                DefaultButton = ContentDialogButton.Close,
                MinWidth = 800,
                MinHeight = 600,
                MaxWidth = 1200,
                MaxHeight = 900
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var logBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += async (s, e) => 
            {
                if (dialog.XamlRoot == null)
                {
                    timer.Stop();
                    return;
                }
                
                var path = _logManager.GetLatestLogPath(serviceId);
                if (path != null)
                {
                    var newText = await _logManager.ReadLogAsync(path);
                    if (logBox.Text != newText) 
                    {
                        logBox.Text = newText;
                        logBox.Select(logBox.Text.Length, 0);
                    }
                }
            };
            timer.Start();
            
            var logPath = _logManager.GetLatestLogPath(serviceId);
            if (logPath != null)
            {
                logBox.Text = await _logManager.ReadLogAsync(logPath);
            }
            else
            {
                logBox.Text = "未找到日志文件。请确保服务已启动并生成日志。\n日志路径: " + _logManager.GetLogDirectory();
            }

            Grid.SetRow(logBox, 0);
            grid.Children.Add(logBox);

            var btnStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };

            var refreshBtn = new Button 
            { 
                Content = "刷新", 
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 6, 12, 6)
            };
            refreshBtn.Click += async (s, e) => 
            {
                var path = _logManager.GetLatestLogPath(serviceId);
                logBox.Text = path != null ? await _logManager.ReadLogAsync(path) : "未找到日志文件。";
            };

            var openFolderBtn = new Button 
            { 
                Content = "打开文件夹", 
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 6, 12, 6)
            };
            openFolderBtn.Click += async (s, e) => 
            {
                var folder = _logManager.GetLogDirectory();
                if (System.IO.Directory.Exists(folder))
                {
                    await Windows.System.Launcher.LaunchFolderPathAsync(folder);
                }
            };

            var closeBtn = new Button 
            { 
                Content = "关闭", 
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 6, 12, 6)
            };
            closeBtn.Click += (s, e) => dialog.Hide();

            btnStack.Children.Add(openFolderBtn);
            btnStack.Children.Add(refreshBtn);
            btnStack.Children.Add(closeBtn);

            Grid.SetRow(btnStack, 1);
            grid.Children.Add(btnStack);

            dialog.Content = grid;
            dialog.CloseButtonText = "";
            dialog.PrimaryButtonText = ""; 
            
            await dialog.ShowAsync();
            timer.Stop();
        }

        private async void OnAddServiceClick(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "添加新服务",
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };

            var stack = new StackPanel { Spacing = 10 };
            var nameBox = new TextBox { Header = "服务名称 (仅字母数字)", PlaceholderText = "MyService" };
            var exeBox = new TextBox { Header = "可执行文件路径", PlaceholderText = "C:\\Path\\To\\App.exe" };
            var argsBox = new TextBox { Header = "启动参数 (可选)" };
            
            var browseBtn = new Button { Content = "浏览..." };
            browseBtn.Click += async (s, args) => {
                string? pickedPath = null;
                try
                {
                    var picker = new FileOpenPicker();
                    picker.ViewMode = PickerViewMode.List;
                    picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
                    picker.FileTypeFilter.Add(".exe");
                    picker.FileTypeFilter.Add(".bat");
                    picker.FileTypeFilter.Add(".cmd");
                    
                    var hwnd = WindowNative.GetWindowHandle(this);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                    var file = await picker.PickSingleFileAsync();
                    if (file != null) pickedPath = file.Path;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WinUI Picker failed: {ex}");
                    pickedPath = await PickFileWithPowerShell();
                }

                if (pickedPath != null) exeBox.Text = pickedPath;
            };

            stack.Children.Add(nameBox);
            stack.Children.Add(exeBox);
            stack.Children.Add(browseBtn);
            stack.Children.Add(argsBox);
            dialog.Content = stack;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(exeBox.Text))
                {
                    await ShowDialog("验证错误", "服务名称和可执行文件路径为必填项。");
                    return;
                }

                try
                {
                    var config = new ServiceConfig
                    {
                        Name = nameBox.Text,
                        ExePath = exeBox.Text,
                        Args = argsBox.Text
                    };
                    await _serviceManager.CreateServiceAsync(config);
                    LoadServices();
                    UpdateStatus($"服务 {config.Name} 已创建。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"创建服务失败: {ex.Message}");
                }
            }
        }

        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "应用设置",
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };

            var stack = new StackPanel { Spacing = 15, Padding = new Thickness(0, 10, 0, 0) };

            var infoBlock = new TextBlock 
            { 
                Text = "本应用用于管理系统后台服务。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8
            };
            
            var retentionHeader = new TextBlock { Text = "日志保留策略", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
            var retentionStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var retentionBox = new NumberBox 
            { 
                Value = LogManager.GetGlobalRetentionDays(),
                Minimum = 1,
                Maximum = 365,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Width = 120,
                Header = "保留天数"
            };
            
            stack.Children.Add(infoBlock);
            stack.Children.Add(new MenuFlyoutSeparator());
            stack.Children.Add(retentionHeader);
            stack.Children.Add(retentionBox);
            stack.Children.Add(new TextBlock { Text = "注: 修改将在服务重启后生效。", FontSize = 12, Opacity = 0.6 });

            dialog.Content = stack;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    int days = (int)retentionBox.Value;
                    LogManager.SetGlobalRetentionDays(days);
                    _logManager.CleanupOldLogs(days);
                    UpdateStatus($"设置已保存。已清理早于 {days} 天的日志。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"保存设置失败: {ex.Message}");
                }
            }
        }

        private async void OnEnvVarsClick(object sender, RoutedEventArgs e)
        {
             var dialog = new ContentDialog
            {
                Title = "添加到 PATH 环境变量",
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };

            var stack = new StackPanel { Spacing = 10 };
            var pathBox = new TextBox { Header = "目录路径", PlaceholderText = "C:\\My\\Tools" };
            var browseBtn = new Button { Content = "浏览目录..." };
            
            browseBtn.Click += async (s, args) => {
                string? pickedPath = null;
                try
                {
                    var picker = new FolderPicker();
                    picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
                    picker.FileTypeFilter.Add("*");
                    
                    var hwnd = WindowNative.GetWindowHandle(this);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                    var folder = await picker.PickSingleFolderAsync();
                    if (folder != null) pickedPath = folder.Path;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WinUI Picker failed: {ex}");
                    pickedPath = await PickFolderWithPowerShell();
                }
                
                if (pickedPath != null) pathBox.Text = pickedPath;
            };

            stack.Children.Add(pathBox);
            stack.Children.Add(browseBtn);
            dialog.Content = stack;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (string.IsNullOrWhiteSpace(pathBox.Text)) return;

                try
                {
                    _envManager.AddToPath(pathBox.Text);
                    UpdateStatus("环境变量已更新。");
                    await ShowDialog("成功", "路径添加成功。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"更新环境变量失败: {ex.Message}");
                }
            }
        }

        private async void OnEditClick(object sender, RoutedEventArgs e)
        {
            await ShowDialog("提示", "编辑功能暂未实现。");
        }

        private async Task ShowDialog(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async Task<bool> ShowConfirmDialog(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = "是",
                CloseButtonText = "否",
                XamlRoot = this.Content.XamlRoot
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        
        private async Task<string?> PickFileWithPowerShell()
        {
            try
            {
                var script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.OpenFileDialog; $f.Filter = 'Executables|*.exe;*.bat;*.cmd|All files|*.*'; if ($f.ShowDialog() -eq 'OK') { $f.FileName }";
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
                }
            }
            catch
            {
            }
            return null;
        }

        private async Task<string?> PickFolderWithPowerShell()
        {
            try
            {
                var script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.FolderBrowserDialog; if ($f.ShowDialog() -eq 'OK') { $f.SelectedPath }";
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
                }
            }
            catch
            {
            }
            return null;
        }
    }
}
