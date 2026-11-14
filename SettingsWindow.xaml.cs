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
        var dark = App.LightTheme ? 0 : 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            target.BackgroundColor = on ? System.Windows.Media.Colors.Transparent : System.Windows.SystemColors.WindowColor;
        var margins = new Margins { Left = on ? -1 : 0, Right = on ? -1 : 0, Top = on ? -1 : 0, Bottom = on ? -1 : 0 };
        DwmExtendFrameIntoClientArea(handle, ref margins);
        var backdrop = on ? 3 : 1; // DWMWA_SYSTEMBACKDROP_TYPE: acrylic / none
        DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        if (on)
        {
            Background = System.Windows.Media.Brushes.Transparent;
            var tint = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(App.LightTheme ? "#F2F6F6F6" : "#66121617"));
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
        SyncDesktopList();
        syncing = false;
    }

    /// <summary>One shortcut field per desktop number, kept whether or not that desktop exists right now.</summary>
    void SyncDesktopList()
    {
        if (recording != null) return; // rebuilding would drop the field being recorded
        DesktopList.Children.Clear();
        for (var i = 0; i < 9; i++)
        {
            if (i > 0) DesktopList.Children.Add(new Border { Style = (Style)FindResource("RowLine") });
            var label = $"데스크탑 {i + 1}";
            var field = new Button { Style = (Style)FindResource("KeyButton"), Content = VirtualDesktops.Describe(config.DesktopShortcuts[i]), Tag = i };
            System.Windows.Automation.AutomationProperties.SetName(field, label);
            field.Click += DesktopShortcut_Click;
            DockPanel.SetDock(field, Dock.Right);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = label });
            if (VirtualDesktops.Failed[i])
                text.Children.Add(new TextBlock
                {
                    Text = "다른 앱이 이미 사용 중인 단축키입니다.", FontSize = 11,
                    Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#FF3B30")!,
                });
            var row = new DockPanel { Style = (Style)FindResource("Row") };
            row.Children.Add(field);
            row.Children.Add(text);
            DesktopList.Children.Add(row);
        }
        DesktopCount.Text = $"현재 데스크탑은 {Math.Max(VirtualDesktops.List().Length, 1)}개입니다. Ctrl+Win+D로 새 데스크탑을 추가할 수 있습니다.";
    }

    Button? recording;

    /// <summary>
    /// Records the next combination pressed on the field: Esc cancels, Backspace or Delete clears it. Mindows lets go of its
    /// shortcuts meanwhile, or pressing one would switch desktops instead of reaching the field.
    /// </summary>
    void DesktopShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (recording != null) return;
        var field = recording = (Button)sender;
        var i = (int)field.Tag;
        VirtualDesktops.Apply(register: false);
        field.Content = "키 입력…";
        field.Focus();

        void End(DesktopShortcut? set)
        {
            field.PreviewKeyDown -= OnKey;
            field.LostKeyboardFocus -= OnLost;
            recording = null;
            if (set != null)
            {
                // One combination switches to one desktop, as on macOS.
                if (set.Key != Key.None)
                    foreach (var other in config.DesktopShortcuts.Where(o => o.Key == set.Key && o.Modifiers == set.Modifiers)) other.Key = Key.None;
                config.DesktopShortcuts[i] = set;
                config.Save();
            }
            VirtualDesktops.Apply();
            SyncDesktopList();
        }
        void OnKey(object _, KeyEventArgs k)
        {
            k.Handled = true; // also keeps Enter and Space from clicking the field again
            var key = k.Key switch { Key.System => k.SystemKey, Key.ImeProcessed => k.ImeProcessedKey, _ => k.Key };
            var mods = Keyboard.Modifiers;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            if (mods == 0 && key == Key.Escape) End(null);
            else if (mods == 0 && key is Key.Back or Key.Delete) End(new DesktopShortcut());
            // Shift alone would take a key away from typing.
            else if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) End(new DesktopShortcut { Modifiers = mods, Key = key });
        }
        // Clicking elsewhere or leaving the window cancels, and gives the shortcuts back.
        void OnLost(object _, KeyboardFocusChangedEventArgs k) => End(null);
        field.PreviewKeyDown += OnKey;
        field.LostKeyboardFocus += OnLost;
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
                foreach (var b in MenuBarWindow.All) b.ApplyTray();
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
            foreach (var b in MenuBarWindow.All) b.ApplyTray();
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

    /// <summary>White over a dark strip of the wallpaper and black over a light one; the strip is given in screen DIPs, as if the picture were stretched to the screen.</summary>
    internal static System.Windows.Media.Brush TextOver(BitmapImage image, double top, double height)
    {
        var scale = image.PixelHeight / SystemParameters.PrimaryScreenHeight;
        var first = Math.Clamp((int)Math.Round(top * scale), 0, image.PixelHeight - 1);
        var rows = Math.Clamp((int)Math.Round(height * scale), 1, image.PixelHeight - first);
        var pixels = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var bgra = new byte[pixels.PixelWidth * 4 * rows];
        pixels.CopyPixels(new Int32Rect(0, first, pixels.PixelWidth, rows), bgra, pixels.PixelWidth * 4, 0);
        double sum = 0;
        for (var i = 0; i < bgra.Length; i += 4) sum += 0.0722 * bgra[i] + 0.7152 * bgra[i + 1] + 0.2126 * bgra[i + 2];
        return sum / (bgra.Length / 4) < 128 ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
    }

    // While the thumb is dragged the dock resizes live; the drop (or a click or key press) re-reserves the strip and saves.
    void IconSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        IconSizeBox.Text = $"{e.NewValue:0}";
        if (!syncing) foreach (var d in DockWindow.All) d.SetIconSize(e.NewValue, commit: Mouse.Captured is not Thumb);
    }

    void IconSize_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        foreach (var d in DockWindow.All) d.SetIconSize(IconSize.Value, commit: true);
    }

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
        foreach (var b in MenuBarWindow.All) b.UpdateForeground();
    }

    void Magnification_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in DockWindow.All) d.SetMagnification(Magnification.IsChecked == true);
    }

    void AutoHide_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in DockWindow.All) d.SetAutoHide(AutoHide.IsChecked == true);
    }

    // The dock reads this on its next slide, so there is nothing to apply.
    void AutoHideAnimation_Click(object sender, RoutedEventArgs e)
    {
        config.DockAutoHideAnimation = AutoHideAnimation.IsChecked == true;
        config.Save();
    }
}
