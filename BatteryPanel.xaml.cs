using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static Mindows.Native;

namespace Mindows;

/// <summary>
/// The panel under the menu bar battery: charge, time left or to full, and the Windows power mode. Windows has no
/// battery flyout of its own, so this is drawn here and hosted by a menu (see <see cref="Item"/>).
/// </summary>
public partial class BatteryPanel : UserControl
{
    const double TrackWidth = 260; // Width minus the 14 of margin on each side

    // Overlay schemes behind Settings > System > Power > Power mode; balanced is the empty GUID.
    static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a"), Balanced = Guid.Empty, Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public BatteryPanel()
    {
        InitializeComponent();
        ModeEfficiency.Tag = Efficiency;
        ModeBalanced.Tag = Balanced;
        ModePerformance.Tag = Performance;
        timer.Tick += (_, _) => Update();
        Loaded += (_, _) => timer.Start();
        Unloaded += (_, _) => timer.Stop();
        MouseLeftButtonUp += (_, e) => e.Handled = true; // a click on empty space must not "choose" the host menu item
        Update();
    }

    /// <summary>The panel as a menu entry that draws only the panel: no highlight, no focus.</summary>
    public static MenuItem Item() => new() { Header = new BatteryPanel(), Style = (Style)Application.Current.FindResource("PanelItem"), Focusable = false };

    void Update()
    {
        GetSystemPowerStatus(out var power);
        var percent = Math.Min((int)power.BatteryLifePercent, 100);
        var onAc = power.ACLineStatus == 1;
        var charging = (power.BatteryFlag & 8) != 0;

        State.Text = charging ? "충전 중" : !onAc ? "배터리 사용 중" : percent >= 100 ? "완충됨" : "전원 연결됨";
        Percent.Text = percent.ToString();
        Bolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
        Fill.Width = TrackWidth * percent / 100.0;
        Fill.Background = onAc ? Brushes.LimeGreen : percent <= 20 ? Brushes.OrangeRed : (Brush)FindResource("MenuFg");
        Source.Text = onAc ? "전원 어댑터" : "배터리";

        var (label, value) = charging ? ("완충까지", MinutesToFull() is { } toFull ? Duration(toFull) : null)
            : onAc ? ("완충까지", percent >= 100 ? "완료" : null)
            : ("남은 시간", power.BatteryLifeTime >= 0 ? Duration(power.BatteryLifeTime / 60) : null); // -1: Windows has no estimate yet
        TimeRow.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
        TimeLabel.Text = label;
        TimeValue.Text = value;

        // An overlay other than the three shown (e.g. Max performance) leaves every segment unchecked.
        var mode = PowerGetEffectiveOverlayScheme(out var scheme) == 0 ? scheme : (Guid?)null;
        foreach (var button in new[] { ModeEfficiency, ModeBalanced, ModePerformance })
            button.IsChecked = mode == (Guid)button.Tag;
    }

    static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes}분" : minutes % 60 == 0 ? $"{minutes / 60}시간" : $"{minutes / 60}시간 {minutes % 60}분";

    /// <summary>SYSTEM_POWER_STATUS has no time to full, so derive it from the battery's own report; null when the battery does not say.</summary>
    static int? MinutesToFull()
    {
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (report.ChargeRateInMilliwatts is > 0 and var rate && report.FullChargeCapacityInMilliwattHours is { } full && report.RemainingCapacityInMilliwattHours is { } left)
                return (int)Math.Ceiling((full - left) * 60.0 / rate);
        }
        catch (Exception) { } // no battery report on this machine
        return null;
    }

    void Mode_Click(object sender, RoutedEventArgs e)
    {
        PowerSetActiveOverlayScheme((Guid)((RadioButton)sender).Tag);
        Update(); // shows what Windows actually applied, e.g. when the call was refused
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if ((Parent as MenuItem)?.Parent is ContextMenu menu) menu.IsOpen = false;
        Sys.Launch("ms-settings:batterysaver");
    }
}
