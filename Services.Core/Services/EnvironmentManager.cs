using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Services.Core.Services
{
    public class EnvironmentManager
    {
        private const int HWND_BROADCAST = 0xffff;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint Msg,
            UIntPtr wParam,
            string lParam,
            uint fuFlags,
            uint uTimeout,
            out UIntPtr lpdwResult);

        public void AddToPath(string path)
        {
            const string keyName = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
            using (var key = Registry.LocalMachine.OpenSubKey(keyName, true))
            {
                if (key == null) throw new Exception("Cannot open Environment registry key");

                var currentPath = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (string.IsNullOrEmpty(currentPath)) currentPath = "";

                var paths = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in paths)
                {
                    if (string.Equals(p.Trim(), path.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                var newPath = currentPath.TrimEnd(';') + ";" + path;
                key.SetValue("Path", newPath, RegistryValueKind.ExpandString);
                
                BroadcastEnvironmentChange();
            }
        }

        private void BroadcastEnvironmentChange()
        {
            try
            {
                SendMessageTimeout(
                    (IntPtr)HWND_BROADCAST,
                    WM_SETTINGCHANGE,
                    UIntPtr.Zero,
                    "Environment",
                    SMTO_ABORTIFHUNG,
                    5000,
                    out _);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to broadcast environment change: {ex.Message}");
            }
        }
    }
}
