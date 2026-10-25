using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using static Mindows.Native;

namespace Mindows;

/// <summary>Mindows' own settings, one sidebar category per TabItem. Changes apply and save at once.</summary>
public partial class SettingsWindow : Window
{
    static SettingsWindow? open;

    readonly Config config;
    readonly DockWindow dock;
    bool syncing = true; // InitializeComponent coerces the slider to its minimum; that is not a user change

    public static void Open(Config config, DockWindow dock)
    {
        open ??= new SettingsWindow(config, dock);
        open.Show();
        // The taskbar is hidden and the dock skips Mindows' windows, so restoring here is the only way back.
        if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
        open.Activate();
    }

    SettingsWindow(Config config, DockWindow dock)
    {
        (this.config, this.dock) = (config, dock);
        InitializeComponent();
        Sync();
        Activated += (_, _) => Sync(); // the dock's resize handle changes the size too
        Closed += (_, _) => open = null;
        SourceInitialized += (_, _) =>
        {
            var dark = App.LightTheme ? 0 : 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        };
    }

    void Sync()
    {
        syncing = true;
        IconSize.Value = config.DockIconSize;
        IconSizeBox.Text = $"{IconSize.Value:0}";
        Magnification.IsChecked = config.DockMagnification;
        AutoHide.IsChecked = config.DockAutoHide;
        AutoHideAnimation.IsChecked = config.DockAutoHideAnimation;
        syncing = false;
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

    void IconSizeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitIconSizeBox();
    void Magnification_Click(object sender, RoutedEventArgs e) => dock.SetMagnification(Magnification.IsChecked == true);
    void AutoHide_Click(object sender, RoutedEventArgs e) => dock.SetAutoHide(AutoHide.IsChecked == true);

    // The dock reads this on its next slide, so there is nothing to apply.
    void AutoHideAnimation_Click(object sender, RoutedEventArgs e)
    {
        config.DockAutoHideAnimation = AutoHideAnimation.IsChecked == true;
        config.Save();
    }
}
