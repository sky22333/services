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

                // If log file changed (rotated) or force reload
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

                // If file shrunk, it was truncated/rotated
                if (fs.Length < _lastPosition)
                {
                    _logEntries.Clear();
                    _lastPosition = 0;
                }

                if (fs.Length > _lastPosition)
                {
                    fs.Seek(_lastPosition, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs);

                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        _logEntries.Add(line);
                    }
                    _lastPosition = fs.Position;

                    // Scroll to bottom
                    if (_logEntries.Count > 0)
                    {
                        LogListView.ScrollIntoView(_logEntries.Last());
                    }
                }
            }
            catch (Exception ex)
            {
                // Ignore read errors (e.g. file locked)
                System.Diagnostics.Debug.WriteLine($"Read log failed: {ex.Message}");
            }
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