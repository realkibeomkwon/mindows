using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ManagedShell;
using ManagedShell.WindowsTray;
using Microsoft.Win32;

namespace Mindows;

public partial class App : Application
{
    Mutex? instance;
    bool started; // false for --restore and a second instance, which must not tear down the running one's taskbar state
    static ShellManager? shell;
    public static bool LightTheme { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Recovery path after a hard kill: bring the taskbar back and quit.
        if (e.Args.Contains("--restore"))
        {
            Taskbar.Restore();
            Taskbar.BroadcastTaskbarCreated(); // tray icons were registered with our dead tray host
            Shutdown();
            return;
        }
        instance = new Mutex(true, "Mindows.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }
        started = true;

        DispatcherUnhandledException += (_, a) => Crash(a.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Crash(a.ExceptionObject);
        SessionEnding += (_, _) => Taskbar.Restore();

        // The bars run all session: software rendering keeps about 40 MB less than the GPU path, at the same CPU while the dock animates.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var config = Config.Load();
        ApplyTheme(config.MenuBarTransparent);
        // Which category Windows reports a light/dark switch under is not documented, so every change re-reads the value.
        SystemEvents.UserPreferenceChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => { if (ReadLightTheme() != LightTheme) ApplyTheme(config.MenuBarTransparent); });
        Taskbar.WaitForExplorer();
        // Hide first: the tray host takes over the Shell_TrayWnd class, and the state file must hold Explorer's state.
        Taskbar.Hide();
        shell = StartTrayHost(config.TrayOrder);
        var dock = new DockWindow(config) { Tray = shell?.NotificationArea };
        Action openSettings = () => SettingsWindow.Open(config, shell?.NotificationArea);
        var bar = new MenuBarWindow(shell?.NotificationArea, config, dock, openSettings);
        bar.Show();
        dock.Show();
        SyncMonitors(config, openSettings);
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => SyncMonitors(config, openSettings));
        HotCorners.Start(config);
        VirtualDesktops.Start(config);
    }

    static App()
    {
        var (add, remove) = MonitorChanges(new[] { "b", "c" }, new[] { "a", "b" });
        Debug.Assert(add.SequenceEqual(new[] { "c" }) && remove.SequenceEqual(new[] { "a" }),
            "a monitor change must add bars for new monitors and remove them for gone ones, leaving the rest alone");
    }

    readonly Dictionary<string, (MenuBarWindow bar, DockWindow dock)> secondaryBars = new();

    /// <summary>Gives every monitor but the primary its own menu bar and dock, and closes them when the monitor goes.</summary>
    void SyncMonitors(Config config, Action openSettings)
    {
        var (add, remove) = MonitorChanges(Native.Monitors().Where(m => !m.primary).Select(m => m.device), secondaryBars.Keys);
        foreach (var device in remove)
        {
            secondaryBars[device].bar.Close();
            secondaryBars[device].dock.Close();
            secondaryBars.Remove(device);
        }
        foreach (var device in add)
        {
            var dock = new DockWindow(config, device) { Tray = shell?.NotificationArea };
            // No tray: the notification area's icons stay on the primary bar.
            var bar = new MenuBarWindow(null, config, dock, openSettings, device);
            secondaryBars[device] = (bar, dock);
            bar.Show();
            dock.Show();
        }
    }

    static (string[] add, string[] remove) MonitorChanges(IEnumerable<string> current, IEnumerable<string> shown) =>
        (current.Except(shown).ToArray(), shown.Except(current).ToArray());

    protected override void OnExit(ExitEventArgs e)
    {
        if (started) Teardown();
        base.OnExit(e);
    }

    /// <summary>
    /// Makes Mindows the notification area host, so apps add their tray icons to the menu bar.
    /// App bar messages it does not handle are forwarded to Explorer.
    /// </summary>
    static ShellManager? StartTrayHost(string[]? trayOrder)
    {
        var config = ShellManager.DefaultShellConfig;
        config.EnableTasksService = false;
        config.PinnedNotifyIcons = trayOrder ?? new[] { NotificationArea.NETWORK_GUID, NotificationArea.VOLUME_GUID };
        // A uiAccess process runs above the apps that talk to it, so UIPI would drop their tray and app bar data (WM_COPYDATA)
        // and the TaskbarCreated broadcast.
        Native.ChangeWindowMessageFilter(0x4A /* WM_COPYDATA */, 1 /* MSGFLT_ADD */);
        Native.ChangeWindowMessageFilter(Native.RegisterWindowMessage("TaskbarCreated"), 1);
        Native.ChangeWindowMessageFilter(Native.RegisterWindowMessage("MindowsAppBar"), 1); // AppBar.CallbackMsg, sent by Explorer
        try { return new ShellManager(config); }
        catch (Exception e) // the bars still work without tray icons
        {
            Log(e);
            return null;
        }
    }

    static void Teardown()
    {
        // Dispose first so app bar and taskbar messages go straight to Explorer; it also re-announces Explorer's tray.
        try { shell?.Dispose(); }
        catch (Exception e) { Log(e); } // restoring the taskbar below matters more
        shell = null;
        HotCorners.Stop(); // gives back the screen saver if the pointer rests in a Disable Screen Saver corner
        AppBar.RemoveAll();
        Taskbar.Restore();
    }

    static void Crash(object error)
    {
        Teardown();
        Log(error);
    }

    static void Log(object error)
    {
        try { File.AppendAllText(Path.Combine(Config.Dir, "crash.log"), $"{DateTime.Now:O} {error}\n\n"); }
        catch (IOException) { }
    }

    static LinearGradientBrush Vertical(params (string color, double offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (color, offset) in stops)
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        brush.Freeze();
        return brush;
    }

    /// <summary>The bar's background alone, for when the setting changes while running. Alpha 1 rather than 0 keeps a transparent bar clickable.</summary>
    public static void ApplyBarBackground(bool transparent)
    {
        var color = transparent ? "#01000000" : LightTheme ? "#FFFFFFFF" : "#FF2B2B2B";
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Current.Resources["BarBg"] = brush;
    }

    static bool ReadLightTheme() => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
        "SystemUsesLightTheme", 0) is 1;

    void ApplyTheme(bool barTransparent)
    {
        LightTheme = ReadLightTheme();
        // WindowBg is opaque: normal (non-layered) windows draw translucent backgrounds over black.
        var colors = LightTheme
            ? new[] { "#B8F4F4F4", "#FF1C1C1C", "#F2F6F6F6", "#FF1C1C1C", "#22000000", "#1F000000", "#FFFFFFFF", "#FFF1EFEE", "#FFF7F7F7", "#FF007AFF" }
            : new[] { "#8C1E1E1E", "#FFF5F5F5", "#F22B2B2B", "#FFF0F0F0", "#FF3D4040", "#33FFFFFF", "#FF222528", "#FF121617", "#FF292C2E", "#FF0A84FF" };
        string[] keys = { "BarBg", "BarFg", "MenuBg", "MenuFg", "MenuBorder", "Hover", "WindowBg", "PaneBg", "CardBg", "Accent" };
        for (var i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            Resources[keys[i]] = brush;
        }
        // Dock name tag: black text on the light pill, white text on the dark one.
        var tagFg = new SolidColorBrush(LightTheme ? Colors.Black : Colors.White);
        tagFg.Freeze();
        Resources["TagFg"] = tagFg;
        // Light glass is a faint white wash. Dark glass lays a charcoal veil over the same blur, so the shelf reads darker without hiding the wallpaper.
        Resources["ShelfFill"] = LightTheme
            ? Vertical(("#14FFFFFF", 0), ("#1AFFFFFF", 1))
            : Vertical(("#40121617", 0), ("#6B121617", 1));
        // The rim is lit from above. In the dark theme the lower edge is dimmer, so it does not turn the shelf milky.
        Resources["ShelfRim"] = LightTheme
            ? Vertical(("#B3FFFFFF", 0), ("#1AFFFFFF", 0.3), ("#1AFFFFFF", 0.7), ("#8CFFFFFF", 1))
            : Vertical(("#A6FFFFFF", 0), ("#14FFFFFF", 0.3), ("#14FFFFFF", 0.7), ("#59FFFFFF", 1));
        ApplyBarBackground(barTransparent);
    }
}
