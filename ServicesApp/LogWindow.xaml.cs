using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using System;
using Services.Core.Services;
using WinRT.Interop;

namespace ServicesApp
{
    public sealed partial class LogWindow : Window
    {
        private readonly LogManager _logManager;
        private readonly string _serviceId;
        private readonly DispatcherTimer _timer;
        private AppWindow _appWindow;

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
            _appWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += OnTimerTick;

            this.Closed += (s, e) => _timer.Stop();

            LoadLog();
            _timer.Start();
        }

        private async void OnTimerTick(object? sender, object e)
        {
            var path = _logManager.GetLatestLogPath(_serviceId);
            if (path != null)
            {
                var newText = await _logManager.ReadLogAsync(path);
                if (LogTextBox.Text != newText)
                {
                    LogTextBox.Text = newText;
                    LogTextBox.Select(LogTextBox.Text.Length, 0);
                }
            }
        }

        private async void LoadLog()
        {
            var path = _logManager.GetLatestLogPath(_serviceId);
            if (path != null)
            {
                LogTextBox.Text = await _logManager.ReadLogAsync(path);
                LogTextBox.Select(LogTextBox.Text.Length, 0);
            }
            else
            {
                LogTextBox.Text = "未找到日志文件。请确保服务已启动并生成日志。\n日志路径: " + _logManager.GetLogDirectory();
            }
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            LoadLog();
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