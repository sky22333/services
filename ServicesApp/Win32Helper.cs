using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace ServicesApp
{
    public static class Win32Helper
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetActiveWindow();

        public static string? PickFile(IntPtr owner, string title, string filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*")
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                dialog.SetTitle(title);
                dialog.SetOptions(FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST);

                var rgSpec = new[]
                {
                    new COMDLG_FILTERSPEC { pszName = "Executable Files", pszSpec = "*.exe;*.bat;*.cmd" },
                    new COMDLG_FILTERSPEC { pszName = "All Files", pszSpec = "*.*" }
                };
                dialog.SetFileTypes((uint)rgSpec.Length, rgSpec);

                if (dialog.Show(owner) == 0)
                {
                    dialog.GetResult(out var item);
                    if (item != null)
                    {
                        item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
                        return path;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Win32 Dialog failed: {ex}");
            }
            return null;
        }

        public static string? PickFolder(IntPtr owner, string title)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                dialog.SetTitle(title);
                dialog.SetOptions(FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST);

                if (dialog.Show(owner) == 0)
                {
                    dialog.GetResult(out var item);
                    if (item != null)
                    {
                        item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
                        return path;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Win32 Folder Dialog failed: {ex}");
            }
            return null;
        }

        [ComImport]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialog
        {
        }

        [ComImport]
        [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show([In] IntPtr parent);
            void SetFileTypes([In] uint cFileTypes, [In, MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
            void SetFileTypeIndex([In] uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise([In, MarshalAs(UnmanagedType.Interface)] object pfde, out uint pdwCookie);
            void Unadvise([In] uint dwCookie);
            void SetOptions([In] FILEOPENDIALOGOPTIONS fos);
            void GetOptions(out FILEOPENDIALOGOPTIONS pfos);
            void SetDefaultFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);
            void SetFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);
            void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void AddPlace([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi, int fdap);
            void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close([MarshalAs(UnmanagedType.Error)] int hr);
            void SetClientGuid([In] ref Guid guid);
            void ClearClientData();
            void SetFilter([MarshalAs(UnmanagedType.Interface)] object pFilter);
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler([In, MarshalAs(UnmanagedType.Interface)] IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
            void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void GetDisplayName([In] SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);
            void Compare([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi, [In] uint hint, out int piOrder);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct COMDLG_FILTERSPEC
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
        }

        [Flags]
        private enum FILEOPENDIALOGOPTIONS : uint
        {
            FOS_FORCEFILESYSTEM = 0x00000040,
            FOS_PICKFOLDERS = 0x00000020,
            FOS_PATHMUSTEXIST = 0x00000800,
            FOS_FILEMUSTEXIST = 0x00001000,
        }

        private enum SIGDN : uint
        {
            SIGDN_FILESYSPATH = 0x80058000,
        }
    }
}
