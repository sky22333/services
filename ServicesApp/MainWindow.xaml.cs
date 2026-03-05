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

namespace ServicesApp
{
    public sealed partial class MainWindow : Window
    {
        private H.NotifyIcon.TaskbarIcon? TrayIcon;
        private readonly WindowsServiceManager _serviceManager;
        private readonly EnvironmentManager _envManager;
        private readonly LogManager _logManager;
        private AppWindow _appWindow;
        private bool _isRealExit = false;
        private DispatcherTimer? _refreshTimer;
        private bool _isLoadServicesRunning = false;

        public ObservableCollection<Service> Services { get; } = new();

        public MainWindow()
        {
            this.InitializeComponent();
            ((FrameworkElement)this.Content).DataContext = this;

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Resize(new Windows.Graphics.SizeInt32(1800, 1200));

            // Hide window instead of closing
            _appWindow.Closing += (s, args) =>
            {
                if (!_isRealExit)
                {
                    args.Cancel = true;
                    _appWindow.Hide();
                    UpdateTimerState(false);
                }
            };

            // Handle Minimize/Restore events to optimize resource usage
            _appWindow.Changed += (s, args) =>
            {
                if (args.DidPresenterChange)
                {
                    var presenter = _appWindow.Presenter as OverlappedPresenter;
                    if (presenter != null)
                    {
                        if (presenter.State == OverlappedPresenterState.Minimized)
                        {
                            UpdateTimerState(false);
                        }
                        else
                        {
                            UpdateTimerState(true);
                        }
                    }
                }
            };

            _serviceManager = new WindowsServiceManager();
            _serviceManager.ServiceUpdated += OnServiceUpdated;

            _envManager = new EnvironmentManager();
            _logManager = new LogManager();

            // Perform initialization after the window is loaded to improve startup performance
            this.Activated += OnWindowActivated;

            Title = "ServicesApp";

            this.Closed += OnWindowClosed;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += async (s, e) =>
            {
                // Lightweight status refresh only
                await _serviceManager.RefreshServiceStatusesAsync();
            };
            _refreshTimer.Start();
        }

        private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            this.Activated -= OnWindowActivated;

            // Initialize tray icon first as it's UI related
            InitializeTrayIcon();

            // Load full configuration ONCE on startup
            await _serviceManager.InitializeAsync();
            LoadServices();
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;
            if (_serviceManager != null)
            {
                _serviceManager.ServiceUpdated -= OnServiceUpdated;
                _serviceManager.Dispose();
            }
            TrayIcon?.Dispose();
        }

        private void InitializeTrayIcon()
        {
            try
            {
                TrayIcon = new H.NotifyIcon.TaskbarIcon();
                TrayIcon.ToolTipText = "ServicesApp";

                // Use absolute path for icon
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    TrayIcon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
                }

                // Important: Add to visual tree FIRST to ensure it has a XamlRoot and Dispatcher
                RootGrid.Children.Add(TrayIcon);

                // Use SecondWindow mode to support context menu in unpackaged apps
                // Optimization: Switch to PopupMenu mode to guarantee no scrollbars and native performance
                TrayIcon.ContextMenuMode = H.NotifyIcon.ContextMenuMode.PopupMenu;
                TrayIcon.NoLeftClickDelay = true; // Improve responsiveness

                // Setup events
                TrayIcon.DoubleTapped += (s, e) => ShowWindow();

                // Initialize Flyout
                var flyout = new MenuFlyout();

                // No manual style needed for PopupMenu mode
                // flyout.Placement is ignored in PopupMenu mode

                // Use XamlUICommand to ensure events are fired correctly in PopupMenu mode
                var showCommand = new XamlUICommand();
                showCommand.Label = "显示窗口";
                showCommand.ExecuteRequested += (s, e) => ShowWindow();

                var showItem = new MenuFlyoutItem { Text = "显示窗口", Command = showCommand };

                var exitCommand = new XamlUICommand();
                exitCommand.Label = "退出";
                exitCommand.ExecuteRequested += (s, e) => RealExit();

                var exitItem = new MenuFlyoutItem { Text = "退出", Command = exitCommand };

                // Note: FontIcon is not supported in PopupMenu mode natively without bitmap conversion.
                // Keeping it simple for now to ensure performance and no visual glitches.

                flyout.Items.Add(showItem);
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(exitItem);

                TrayIcon.ContextFlyout = flyout;
                TrayIcon.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tray Icon Init Failed: {ex}");
            }
        }

        public void ShowWindow()
        {
            _appWindow.Show();
            _appWindow.MoveInZOrderAtTop();
            this.Activate();
            UpdateTimerState(true);
            LoadServices(); // Refresh immediately when showing
        }

        public void RealExit()
        {
            _isRealExit = true;
            TrayIcon?.Dispose();
            Application.Current.Exit();
        }

        private void OnShowWindowClick(object sender, RoutedEventArgs e)
        {
            ShowWindow();
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            RealExit();
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
            if (_isLoadServicesRunning) return;

            // Optimization: Do not refresh UI if window is hidden and this is an automated refresh
            if (silent && _appWindow != null && !_appWindow.IsVisible) return;

            _isLoadServicesRunning = true;
            try
            {
                if (!silent) UpdateStatus("正在加载服务...");
                var list = await _serviceManager.GetServicesSnapshotAsync();

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
            finally
            {
                _isLoadServicesRunning = false;
            }
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            UpdateStatus("正在刷新服务列表...");
            await _serviceManager.InitializeAsync(); // Force reload from registry AND refresh status atomically
            LoadServices(); // Load the now-complete data into UI
            var count = Services.Count;
            UpdateStatus($"已加载 {count} 个服务。");

            // Manual GC to keep memory footprint low after refresh
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
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

        private void OnLogsClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string serviceId)
            {
                var service = Services.FirstOrDefault(s => s.Id == serviceId);
                if (service != null)
                {
                    ShowLogViewer(service.Id, service.Name);
                }
                else
                {
                    ShowLogViewer(serviceId, serviceId);
                }
            }
        }

        private void ShowLogViewer(string serviceId, string displayName)
        {
            var logWindow = new LogWindow(serviceId, displayName, _logManager);
            logWindow.Activate();
            logWindow.CenterOnScreen(_appWindow);
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
            var workDirBox = new TextBox { Header = "工作目录 (可选)" };

            var startupBox = new ComboBox { Header = "启动类型", HorizontalAlignment = HorizontalAlignment.Stretch };
            startupBox.Items.Add("自动 (开机自启)");
            startupBox.Items.Add("手动");
            startupBox.SelectedIndex = 0;

            var autoRestartCheck = new CheckBox { Content = "失败自动重启 (间隔 5 秒)", IsChecked = false };

            var browseBtn = new Button { Content = "选择程序" };
            browseBtn.Click += (s, args) =>
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var pickedPath = Win32Helper.PickFile(hwnd, "选择可执行文件");

                if (pickedPath != null)
                {
                    exeBox.Text = pickedPath;
                    if (string.IsNullOrWhiteSpace(workDirBox.Text))
                    {
                        workDirBox.Text = System.IO.Path.GetDirectoryName(pickedPath) ?? "";
                    }
                }
            };

            stack.Children.Add(nameBox);
            stack.Children.Add(exeBox);
            stack.Children.Add(browseBtn);
            stack.Children.Add(argsBox);
            stack.Children.Add(workDirBox);
            stack.Children.Add(startupBox);
            stack.Children.Add(autoRestartCheck);
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
                        Args = argsBox.Text,
                        WorkingDir = workDirBox.Text,
                        AutoRestart = autoRestartCheck.IsChecked ?? false,
                        StartupType = (ServiceStartupType)(startupBox.SelectedIndex + 2) // Auto=2, Manual=3
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
                Width = 200,
                VerticalAlignment = VerticalAlignment.Center,
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

            browseBtn.Click += (s, args) =>
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var pickedPath = Win32Helper.PickFolder(hwnd, "选择目录");

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

        private void UpdateTimerState(bool isVisible)
        {
            if (isVisible)
            {
                if (_refreshTimer != null && !_refreshTimer.IsEnabled)
                {
                    _refreshTimer.Start();
                    // Refresh immediately when becoming visible to ensure fresh data
                    _serviceManager.RefreshServiceStatusesAsync();
                }
            }
            else
            {
                if (_refreshTimer != null && _refreshTimer.IsEnabled)
                {
                    _refreshTimer.Stop();
                }
            }
        }
    }
}
