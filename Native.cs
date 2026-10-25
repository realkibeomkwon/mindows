using System.Runtime.InteropServices;
using System.Text;

namespace Mindows;

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
public struct APPBARDATA
{
    public int cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public IntPtr lParam;
}

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_POWER_STATUS
{
    public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
    public int BatteryLifeTime, BatteryFullLifeTime;
}

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x8000000;
    public const int SW_HIDE = 0, SW_MAXIMIZE = 3, SW_SHOW = 5, SW_MINIMIZE = 6, SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x10, GW_OWNER = 4, SWP_NOSIZE = 1, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("win32u.dll")] static extern int NtUserBuildHwndList(IntPtr desktop, IntPtr parent, bool children, bool removeImmersive, uint thread, uint max, [Out] IntPtr[] list, out uint count);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] public static extern int GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("imm32.dll")] public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(long point);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] public static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(IntPtr h, string? root, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr h, ref int len, StringBuilder sb);
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("powrprof.dll")] public static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    /// <summary>Top-level windows including the shell's immersive ones (Search, Start), which EnumWindows leaves out.</summary>
    // shortcut: undocumented win32u call (what EnumWindows uses, minus its immersive filter); on any failure
    // this returns nothing and callers fall back to finding those windows when they come to the front.
    public static IntPtr[] AllTopLevelWindows()
    {
        var list = new IntPtr[8192];
        try
        {
            if (NtUserBuildHwndList(IntPtr.Zero, IntPtr.Zero, false, false, 0, (uint)list.Length, list, out var count) == 0)
                return list[..(int)Math.Min(count, (uint)list.Length)];
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { }
        return Array.Empty<IntPtr>();
    }

    /// <summary>
    /// Top edge, in window pixels, of what another process's window draws down its center column, or with
    /// <paramref name="bottom"/> its bottom edge. Captured with PW_RENDERFULLCONTENT, its transparent parts come out black.
    /// </summary>
    public static int? ContentEdge(IntPtr h, bool bottom)
    {
        GetWindowRect(h, out var r);
        // Only the left half up to the center column is captured: the cost scales with the area, and PrintWindow ignores a viewport offset.
        int w = (r.Right - r.Left) / 2 + 1, height = r.Bottom - r.Top;
        if (w <= 1 || height <= 0) return null;
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var bmp = CreateDIBSection(dc, ref bi, 0, out var bits, IntPtr.Zero, 0); // top-down, so row y starts at y * w
        var old = SelectObject(dc, bmp);
        try
        {
            if (bmp == IntPtr.Zero || !PrintWindow(h, dc, 2)) return null;
            for (var i = 0; i < height; i++)
            {
                var y = bottom ? height - 1 - i : i;
                if ((Marshal.ReadInt32(bits, (y * w + w - 1) * 4) & 0xFFFFFF) != 0) return bottom ? y + 1 : y;
            }
            return null;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bmp);
            DeleteDC(dc);
        }
    }

    /// <summary>Primary monitor bounds in physical pixels and its DPI scale.</summary>
    public static (RECT bounds, double scale) PrimaryMonitor()
    {
        var mon = MonitorFromPoint(0, 1); // MONITOR_DEFAULTTOPRIMARY
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        return (mi.rcMonitor, GetDpiForMonitor(mon, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0);
    }

    /// <summary>Exe path and AppUserModelID (packaged apps only) of a process.</summary>
    public static (string? exe, string? aumid) QueryProcess(uint pid)
    {
        var hp = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (hp == IntPtr.Zero) return (null, null);
        try
        {
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            var exe = QueryFullProcessImageName(hp, 0, sb, ref n) ? sb.ToString() : null;
            int len = 512;
            var id = new StringBuilder(len);
            var aumid = GetApplicationUserModelId(hp, ref len, id) == 0 ? id.ToString() : null;
            return (exe, aumid);
        }
        finally { CloseHandle(hp); }
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT value);

    /// <summary>AppUserModelID set on a window, e.g. by ApplicationFrameHost for its UWP frames.</summary>
    public static string? WindowAumid(IntPtr h)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(h, ref iid, out var store) != 0) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }; // PKEY_AppUserModel_ID
            if (store.GetValue(ref key, out var value) != 0) return null;
            try { return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.p) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")]
    static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr memory);
    static IntPtr wlan;

    /// <summary>Signal quality (0–100) of the connected Wi-Fi network; null when none is connected.</summary>
    public static int? WifiSignal()
    {
        if (wlan == IntPtr.Zero && WlanOpenHandle(2, IntPtr.Zero, out _, out wlan) != 0) return null;
        if (WlanEnumInterfaces(wlan, IntPtr.Zero, out var list) != 0) return null;
        try
        {
            for (var i = 0; i < Marshal.ReadInt32(list); i++)
            {
                var info = list + 8 + i * 532; // WLAN_INTERFACE_INFO: GUID, description[256], state
                if (Marshal.ReadInt32(info, 528) != 1) continue; // wlan_interface_state_connected
                var id = Marshal.PtrToStructure<Guid>(info);
                if (WlanQueryInterface(wlan, ref id, 7, IntPtr.Zero, out _, out var connection, IntPtr.Zero) != 0) continue; // current_connection
                try { return Marshal.ReadInt32(connection, 576); } // WLAN_CONNECTION_ATTRIBUTES.wlanAssociationAttributes.wlanSignalQuality
                finally { WlanFreeMemory(connection); }
            }
            return null;
        }
        finally { WlanFreeMemory(list); }
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(); // never called; keeps GetDisplayName in its vtable slot
        void GetParent();
        [PreserveSig] int GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    /// <summary>Display name of a shell item, e.g. an installed web app's name for its shell:AppsFolder path.</summary>
    public static string? ShellItemName(string path)
    {
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) != 0) return null;
        try { return item.GetDisplayName(0, out var name) == 0 ? name : null; } // SIGDN_NORMALDISPLAY
        finally { Marshal.ReleaseComObject(item); }
    }

    public static string WindowText(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
