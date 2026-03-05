using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Services.Core.Services;

namespace ServicesApp
{
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--service-wrapper")
            {
                // Fix: Ensure CurrentDirectory is set to app folder, not System32
                // This is crucial for loading dependencies correctly when running as a Service
                var module = Process.GetCurrentProcess().MainModule;
                if (module != null)
                {
                    var dir = System.IO.Path.GetDirectoryName(module.FileName);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.SetCurrentDirectory(dir);
                }

                var serviceName = args[1];
                using var wrapper = new EmbeddedServiceWrapper(serviceName);
                ServiceBase.Run(wrapper);
                return;
            }

            const string mutexName = "Global\\Services_App_SingleInstance_Mutex";
            using var mutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                var current = Process.GetCurrentProcess();
                foreach (var process in Process.GetProcessesByName(current.ProcessName))
                {
                    if (process.Id != current.Id)
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindow(process.MainWindowHandle, 9);
                            SetForegroundWindow(process.MainWindowHandle);
                        }
                        break;
                    }
                }
                return;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
