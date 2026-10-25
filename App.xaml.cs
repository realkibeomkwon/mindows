using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Mindows;

public partial class App : Application
{
    Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Recovery path after a hard kill: bring the taskbar back and quit.
        if (e.Args.Contains("--restore"))
        {
            Taskbar.Restore();
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
        Taskbar.Hide();
        new MenuBarWindow().Show();
        new DockWindow(config).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppBar.RemoveAll();
        Taskbar.Restore();
        base.OnExit(e);
    }

    static void Crash(object error)
    {
        AppBar.RemoveAll();
        Taskbar.Restore();
        try { File.AppendAllText(Path.Combine(Config.Dir, "crash.log"), $"{DateTime.Now:O} {error}\n\n"); }
        catch (IOException) { }
    }

    void ApplyTheme()
    {
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is 1;
        var colors = light
            ? new[] { "#B8F4F4F4", "#FF1C1C1C", "#F2F6F6F6", "#FF1C1C1C", "#22000000", "#1F000000", "#73F2F2F2", "#55FFFFFF" }
            : new[] { "#8C1E1E1E", "#FFF5F5F5", "#F22B2B2B", "#FFF0F0F0", "#33FFFFFF", "#33FFFFFF", "#662A2A2A", "#33FFFFFF" };
        string[] keys = { "BarBg", "BarFg", "MenuBg", "MenuFg", "MenuBorder", "Hover", "DockBg", "DockBorder" };
        for (var i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            Resources[keys[i]] = brush;
        }
    }
}
