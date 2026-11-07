using System.Security;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManagedShell.WindowsTray;
using Microsoft.Win32;
using static Mindows.Native;

namespace Mindows;

/// <summary>Mindows' own settings, one sidebar category per TabItem. Changes apply and save at once.</summary>
public partial class SettingsWindow : Window
{
    static SettingsWindow? open;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string TaskName = "Mindows";

    readonly Config config;
    readonly DockWindow dock;
    readonly NotificationArea? tray;
    readonly MenuBarWindow bar;
    bool syncing = true; // InitializeComponent coerces the slider to its minimum; that is not a user change

    public static void Open(Config config, DockWindow dock, NotificationArea? tray, MenuBarWindow bar)
    {
        open ??= new SettingsWindow(config, dock, tray, bar);
        open.Show();
        // The taskbar is hidden and the dock skips Mindows' windows, so restoring here is the only way back.
        if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
        open.Activate();
    }

    SettingsWindow(Config config, DockWindow dock, NotificationArea? tray, MenuBarWindow bar)
    {
        (this.config, this.dock, this.tray, this.bar) = (config, dock, tray, bar);
        InitializeComponent();
        Sync();
        Activated += (_, _) => Sync(); // the dock's resize handle changes the size too
        Closed += (_, _) => open = null;
        SourceInitialized += (_, _) =>
        {
            var dark = App.LightTheme ? 0 : 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            ApplyBackdrop();
        };
        // Windows broadcasts a setting change when its transparency effects are switched.
        UserPreferenceChangedEventHandler onPrefs = (_, _) => Dispatcher.BeginInvoke(() => { Sync(); ApplyBackdrop(); });
        SystemEvents.UserPreferenceChanged += onPrefs;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= onPrefs;
    }

    // Absent means on, as in Windows itself.
    internal static bool WindowsTransparency() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) is not 0;

    /// <summary>Acrylic behind a see-through sidebar while Windows' transparency effects are on; otherwise the opaque look.</summary>
    void ApplyBackdrop()
    {
        var on = WindowsTransparency();
        var handle = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            target.BackgroundColor = on ? System.Windows.Media.Colors.Transparent : System.Windows.SystemColors.WindowColor;
        var margins = new Margins { Left = on ? -1 : 0, Right = on ? -1 : 0, Top = on ? -1 : 0, Bottom = on ? -1 : 0 };
        DwmExtendFrameIntoClientArea(handle, ref margins);
        var backdrop = on ? 3 : 1; // DWMWA_SYSTEMBACKDROP_TYPE: acrylic / none
        DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        if (on)
        {
            Background = System.Windows.Media.Brushes.Transparent;
            var tint = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(App.LightTheme ? "#F2F6F6F6" : "#66202020"));
            tint.Freeze();
            Resources["PaneBg"] = tint;
        }
        else
        {
            SetResourceReference(BackgroundProperty, "WindowBg");
            Resources.Remove("PaneBg");
        }
    }

    void Sync()
    {
        syncing = true;
        // The task itself is the setting, so Config keeps no copy that could drift from it.
        LaunchAtLogin.IsChecked = LogonTaskExists();
        IconSize.Value = config.DockIconSize;
        IconSizeBox.Text = $"{IconSize.Value:0}";
        Magnification.IsChecked = config.DockMagnification;
        AutoHide.IsChecked = config.DockAutoHide;
        AutoHideAnimation.IsChecked = config.DockAutoHideAnimation;
        MenuBarTransparent.IsChecked = config.MenuBarTransparent;
        Translucency.IsChecked = WindowsTransparency(); // the setting is Windows' own, so the switch mirrors it
        for (var i = 0; i < 4; i++) CornerButtons[i].Content = HotCorners.Describe(config.HotCorners[i].Action, config.HotCorners[i].Modifiers);
        WallpaperPreview.Source = Wallpaper();
        MaxTrayBox.Text = $"{config.MaxTrayIcons}";
        SyncTrayList();
        syncing = false;
    }

    /// <summary>One switch per tray icon the apps have added; icons switched off stay listed so they can be switched back on.</summary>
    void SyncTrayList()
    {
        TrayList.Children.Clear();
        var icons = tray?.TrayIcons.Cast<NotifyIcon>().Where(MenuBarWindow.Listed).ToList() ?? new();
        TrayEmpty.Visibility = icons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var icon in icons)
        {
            if (TrayList.Children.Count > 0) TrayList.Children.Add(new Border { Style = (Style)FindResource("RowLine") });
            var name = MenuBarWindow.IconName(icon);
            var row = new DockPanel { Style = (Style)FindResource("Row") };
            var toggle = new CheckBox
            {
                Style = (Style)FindResource("Switch"),
                IsChecked = !config.HiddenTrayIcons.Any(h => MenuBarWindow.SavedAs(icon, h)),
            };
            System.Windows.Automation.AutomationProperties.SetName(toggle, name);
            toggle.Click += (_, _) =>
            {
                var rest = config.HiddenTrayIcons.Where(h => !MenuBarWindow.SavedAs(icon, h));
                config.HiddenTrayIcons = (toggle.IsChecked == true ? rest : rest.Append(icon.Identifier)).ToArray();
                config.Save();
                bar.ApplyTray();
            };
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            TrayList.Children.Add(row);
        }
    }

    /// <summary>Applies a typed maximum; anything that is not a number puts the current one back.</summary>
    void CommitMaxTrayBox()
    {
        if (int.TryParse(MaxTrayBox.Text, out var max) && max >= 0 && max != config.MaxTrayIcons)
        {
            config.MaxTrayIcons = max;
            config.Save();
            bar.ApplyTray();
        }
        MaxTrayBox.Text = $"{config.MaxTrayIcons}";
    }

    void MaxTrayBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitMaxTrayBox();
        MaxTrayBox.SelectAll();
    }

    void MaxTrayBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitMaxTrayBox();

    Button[] CornerButtons => new[] { CornerTopLeft, CornerTopRight, CornerBottomLeft, CornerBottomRight }; // Config.HotCorners order

    /// <summary>
    /// As on macOS, the modifiers held when an action is picked must be held at the corner too; the menu shows them while
    /// they are held.
    /// </summary>
    void Corner_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var corner = config.HotCorners[Array.IndexOf(CornerButtons, button)];
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, HorizontalOffset = -Menus.ShadowMargin, VerticalOffset = -Menus.ShadowMargin };
        var items = new List<(MenuItem item, HotCornerAction action)>();
        foreach (var action in HotCorners.Menu)
        {
            if (action is not { } a)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { IsChecked = a == corner.Action, Padding = new Thickness(26, 0, 12, 0) }; // room for the check, as in Menus.Show
            item.Click += (_, _) =>
            {
                corner.Action = a;
                corner.Modifiers = a == HotCornerAction.None ? 0 : HotCorners.HeldModifiers();
                config.Save();
                HotCorners.Apply();
                Sync();
            };
            menu.Items.Add(item);
            items.Add((item, a));
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        void Relabel()
        {
            HotCorners.MaskWin();
            var held = HotCorners.HeldModifiers();
            foreach (var (item, action) in items) item.Header = HotCorners.Describe(action, held);
        }
        Relabel();
        timer.Tick += (_, _) => Relabel();
        timer.Start();
        menu.Closed += (_, _) => timer.Stop();
        // Alt is a modifier here, not a key that leaves the menu.
        menu.PreviewKeyDown += (_, k) => k.Handled |= k.Key == Key.System;
        menu.PreviewKeyUp += (_, k) => k.Handled |= k.Key == Key.System;
        menu.IsOpen = true;
    }

    /// <summary>The desktop picture as Windows last drew it; null for a solid color or a missing file.</summary>
    internal static BitmapImage? Wallpaper()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper"));
            image.CacheOption = BitmapCacheOption.OnLoad; // Windows rewrites the file when the wallpaper changes
            image.DecodePixelWidth = 300;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // While the thumb is dragged the dock resizes live; the drop (or a click or key press) re-reserves the strip and saves.
    void IconSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        IconSizeBox.Text = $"{e.NewValue:0}";
        if (!syncing) dock.SetIconSize(e.NewValue, commit: Mouse.Captured is not Thumb);
    }

    void IconSize_DragCompleted(object sender, DragCompletedEventArgs e) => dock.SetIconSize(IconSize.Value, commit: true);

    /// <summary>Applies a typed size through the slider, which clamps it; anything else puts the current size back.</summary>
    void CommitIconSizeBox()
    {
        if (int.TryParse(IconSizeBox.Text, out var size)) IconSize.Value = size;
        IconSizeBox.Text = $"{IconSize.Value:0}";
    }

    void IconSizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitIconSizeBox();
        IconSizeBox.SelectAll();
    }

    void LaunchAtLogin_Click(object sender, RoutedEventArgs e)
    {
        // Earlier builds used the Run key, which Explorer starts a minute or more after sign-in.
        Registry.CurrentUser.OpenSubKey(RunKey, writable: true)?.DeleteValue("Mindows", throwOnMissingValue: false);
        if (LaunchAtLogin.IsChecked == true) TaskFolder().RegisterTask(TaskName, LogonTaskXml(), 6 /* CREATE_OR_UPDATE */, null, null, 3 /* INTERACTIVE_TOKEN */);
        else if (LogonTaskExists()) TaskFolder().DeleteTask(TaskName, 0);
    }

    static dynamic TaskFolder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        return service.GetFolder(@"\");
    }

    static bool LogonTaskExists()
    {
        try { TaskFolder().GetTask(TaskName); return true; }
        catch (FileNotFoundException) { return false; }
    }

    /// <summary>
    /// Starts Mindows as soon as the user signs in. The task's defaults would stop a shell after 3 days or on battery,
    /// and a hard stop leaves the taskbar hidden. The task goes through ShellExecute because CreateProcess
    /// fails with 740 on a uiAccess exe.
    /// </summary>
    static string LogonTaskXml()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions><Exec><Command>rundll32.exe</Command><Arguments>url.dll,FileProtocolHandler "{SecurityElement.Escape(Environment.ProcessPath)}"</Arguments></Exec></Actions>
            </Task>
            """;
    }

    void IconSizeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitIconSizeBox();
    // Windows' transparency effects are system-wide; this switch writes the same setting the Settings app does.
    void Translucency_Click(object sender, RoutedEventArgs e)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            key.SetValue("EnableTransparency", Translucency.IsChecked == true ? 1 : 0, RegistryValueKind.DWord);
        var area = System.Runtime.InteropServices.Marshal.StringToHGlobalUni("ImmersiveColorSet");
        try { SendMessageTimeout((IntPtr)0xFFFF, 0x1A /* WM_SETTINGCHANGE */, IntPtr.Zero, area, 2 /* SMTO_ABORTIFHUNG */, 1000, out _); }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(area); }
        ApplyBackdrop();
    }

    void MenuBarTransparent_Click(object sender, RoutedEventArgs e)
    {
        config.MenuBarTransparent = MenuBarTransparent.IsChecked == true;
        config.Save();
        App.ApplyBarBackground(config.MenuBarTransparent);
        bar.UpdateForeground();
    }

    void Magnification_Click(object sender, RoutedEventArgs e) => dock.SetMagnification(Magnification.IsChecked == true);
    void AutoHide_Click(object sender, RoutedEventArgs e) => dock.SetAutoHide(AutoHide.IsChecked == true);

    // The dock reads this on its next slide, so there is nothing to apply.
    void AutoHideAnimation_Click(object sender, RoutedEventArgs e)
    {
        config.DockAutoHideAnimation = AutoHideAnimation.IsChecked == true;
        config.Save();
    }
}
