using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using System;
using System.IO;
using System.Linq;
using System.Collections.ObjectModel;
using Services.Core.Services;
using WinRT.Interop;

namespace ServicesApp
{
    public sealed partial class LogWindow : Window
    {
        private readonly LogManager _logManager;
        private readonly string _serviceId;
        private DispatcherTimer? _timer;
        private AppWindow _appWindow;

        private ObservableCollection<string> _logEntries = new();
        private long _lastPosition = 0;
        private string? _currentLogPath;
        private const int MaxLogLines = 3000;
        private ScrollViewer? _scrollViewer;

        public LogWindow(string serviceId, string displayName, LogManager logManager)
        {
            this.InitializeComponent();
            _serviceId = serviceId;
            _logManager = logManager;

            Title = $"日志 - {displayName}";
            TitleText.Text = Title;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));

            // Bind ListView
            LogListView.ItemsSource = _logEntries;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += OnTimerTick;

            this.Closed += OnWindowClosed;

            LoadLog(true);
            _timer.Start();
        }

        private void OnTimerTick(object? sender, object e)
        {
            LoadLog(false);
        }

        private void OnWindowClosed(object sender, WindowEventArgs e)
        {
            if (_timer != null)
            {
                _timer.Tick -= OnTimerTick;
                _timer.Stop();
                _timer = null;
            }
            this.Closed -= OnWindowClosed;
        }

        private void LoadLog(bool forceReload)
        {
            try
            {
                var path = _logManager.GetLatestLogPath(_serviceId);

                // 文件轮转或强制刷新
                if (path != _currentLogPath || forceReload)
                {
                    _logEntries.Clear();
                    _lastPosition = 0;
                    _currentLogPath = path;
                }

                if (string.IsNullOrEmpty(_currentLogPath) || !File.Exists(_currentLogPath))
                {
                    if (_logEntries.Count == 0) _logEntries.Add($"等待日志生成... (路径: {_logManager.GetLogDirectory()})");
                    return;
                }

                using var fs = new FileStream(_currentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // 文件被截断或轮转
                if (fs.Length < _lastPosition)
                {
                    _logEntries.Clear();
                    _lastPosition = 0;
                }

                if (fs.Length > _lastPosition)
                {
                    bool shouldAutoScroll = IsScrolledToBottom();
                    
                    fs.Seek(_lastPosition, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);

                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        _logEntries.Add(line);
                        
                        if (_logEntries.Count > MaxLogLines)
                        {
                            _logEntries.RemoveAt(0);
                        }
                    }
                    _lastPosition = fs.Position;

                    if (shouldAutoScroll && _logEntries.Count > 0)
                    {
                        LogListView.ScrollIntoView(_logEntries.Last());
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Read log failed: {ex.Message}");
            }
        }

        private bool IsScrolledToBottom()
        {
            if (_scrollViewer == null)
            {
                _scrollViewer = FindVisualChild<ScrollViewer>(LogListView);
            }
            
            if (_scrollViewer == null || _logEntries.Count == 0)
            {
                return true;
            }
            
            return _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 10;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                {
                    return result;
                }
                
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            LoadLog(true);
        }

        private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            var folder = _logManager.GetLogDirectory();
            if (System.IO.Directory.Exists(folder))
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(folder);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        public void CenterOnScreen(AppWindow mainWindow)
        {
            if (mainWindow == null) return;

            var mainPos = mainWindow.Position;
            var mainSize = mainWindow.Size;
            var mySize = _appWindow.Size;

            var x = mainPos.X + (mainSize.Width - mySize.Width) / 2;
            var y = mainPos.Y + (mainSize.Height - mySize.Height) / 2;

            _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
    }
}