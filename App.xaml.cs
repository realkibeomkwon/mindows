using System.Windows;
using System.Windows.Media;
using ManagedShell;
using ManagedShell.WindowsTray;
using Microsoft.Win32;

namespace Mindows;

public partial class App : Application
{
    Mutex? instance;
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

        DispatcherUnhandledException += (_, a) => Crash(a.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Crash(a.ExceptionObject);
        SessionEnding += (_, _) => Taskbar.Restore();

        ApplyTheme();
        var config = Config.Load();
        // Hide first: the tray host takes over the Shell_TrayWnd class, and the state file must hold Explorer's state.
        Taskbar.Hide();
        shell = StartTrayHost(config.TrayOrder);
        var dock = new DockWindow(config);
        new MenuBarWindow(shell?.NotificationArea, config, dock, () => SettingsWindow.Open(config, dock)).Show();
        dock.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Teardown();
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

    void ApplyTheme()
    {
        LightTheme = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is 1;
        // WindowBg is opaque: normal (non-layered) windows draw translucent backgrounds over black.
        var colors = LightTheme
            ? new[] { "#B8F4F4F4", "#FF1C1C1C", "#F2F6F6F6", "#FF1C1C1C", "#22000000", "#1F000000", "#73F2F2F2", "#55FFFFFF", "#FFF4F4F4" }
            : new[] { "#8C1E1E1E", "#FFF5F5F5", "#F22B2B2B", "#FFF0F0F0", "#33FFFFFF", "#33FFFFFF", "#662A2A2A", "#33FFFFFF", "#FF242424" };
        string[] keys = { "BarBg", "BarFg", "MenuBg", "MenuFg", "MenuBorder", "Hover", "DockBg", "DockBorder", "WindowBg" };
        for (var i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            Resources[keys[i]] = brush;
        }
    }
}
