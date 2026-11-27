// Icon extraction helpers (WPF flavor).
//
// Strategy for a high-quality icon:
//   1. Open the .lnk via IShellLink and read its icon location (target exe + icon index).
//   2. Extract the icon with SHDefExtractIcon.
//   3. If that fails, fall back to the shell icon of the .lnk itself.
// The HICON is converted straight to a frozen BitmapSource (GPU-scaled by WPF when drawn).

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameManager;

internal static class ShellIcons
{
    // ---- IShellLink (for resolving .lnk targets) ----

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out short pwHotkey);
        [PreserveSig] int SetHotkey(short wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        [PreserveSig] int GetCurrFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    // ---- Win32 ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(
        string pszIconFile, int iIndex, uint uFlags,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Returns the game's icon as a frozen BitmapSource (never null).</summary>
    public static BitmapSource? GetIconSource(string lnkPath, int size)
    {
        // 1) The game's own icon via the .lnk target/icon location.
        if (TryGetIconSource(lnkPath, out string iconFile, out int iconIndex))
        {
            try
            {
                if (SHDefExtractIcon(iconFile, iconIndex, 0, out IntPtr hIcon, out _, (uint)size) == 0 &&
                    hIcon != IntPtr.Zero)
                {
                    return BitmapFromHandle(hIcon);
                }
            }
            catch
            {
                // fall through to the shell icon
            }
        }

        // 2) Fallback: the shell icon of the shortcut itself.
        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfoW(lnkPath, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON);
        if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            try
            {
                return BitmapFromHandle(info.hIcon);
            }
            catch
            {
                // fall through
            }
        }

        return null;
    }

    private static bool TryGetIconSource(string lnkPath, out string iconFile, out int iconIndex)
    {
        iconFile = string.Empty;
        iconIndex = 0;
        try
        {
            var link = new ShellLink();
            var persist = (IPersistFile)link;
            persist.Load(lnkPath, 0);

            var shell = (IShellLinkW)link;
            var sb = new StringBuilder(1024);
            shell.GetIconLocation(sb, sb.Capacity, out iconIndex);
            string location = sb.ToString();

            if (string.IsNullOrEmpty(location))
            {
                sb.Clear();
                shell.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                location = sb.ToString();
                iconIndex = 0;
            }

            iconFile = location;
            return !string.IsNullOrEmpty(iconFile) && File.Exists(iconFile);
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource BitmapFromHandle(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
