using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Mindows;

public class PinnedApp
{
    public string Name { get; set; } = "";
    /// <summary>Exe path, or shell:AppsFolder\&lt;AUMID&gt; for packaged apps.</summary>
    public string Path { get; set; } = "";
}

public class HotCorner
{
    [JsonConverter(typeof(JsonStringEnumConverter))] public HotCornerAction Action { get; set; }
    /// <summary>Held at the corner as they were when the action was chosen.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))] public ModifierKeys Modifiers { get; set; }
}

public class Config
{
    public const string AppsFolder = @"shell:AppsFolder\";
    public static readonly string Dir = Directory.CreateDirectory(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindows")).FullName;
    static string FilePath => System.IO.Path.Combine(Dir, "config.json");

    public const double MinIconSize = 24, MaxIconSize = 96;

    public List<PinnedApp> Pinned { get; set; } = new();
    /// <summary>Dock icon size at rest, in DIPs.</summary>
    public double DockIconSize { get; set; } = 48;
    public bool DockMagnification { get; set; } = true;
    public bool DockAutoHide { get; set; }
    /// <summary>Slide the auto-hiding dock out and in; off, it disappears and appears at once.</summary>
    public bool DockAutoHideAnimation { get; set; } = true;
    /// <summary>Tray icon identifiers in menu bar order, set by Ctrl+dragging; null keeps network and volume next to the clock.</summary>
    public string[]? TrayOrder { get; set; }
    /// <summary>Top-left, top-right, bottom-left, bottom-right.</summary>
    public HotCorner[] HotCorners { get; set; } = { new(), new(), new(), new() };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) is { } loaded)
            {
                loaded.DockIconSize = Math.Clamp(loaded.DockIconSize, MinIconSize, MaxIconSize); // hand-edited file
                if (loaded.HotCorners is not { Length: 4 } || loaded.HotCorners.Any(c => c is null)) loaded.HotCorners = new Config().HotCorners;
                return loaded;
            }
        }
        catch (JsonException) { File.Move(FilePath, FilePath + ".bad", true); }

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var config = new Config();
        foreach (var (name, path) in new[]
        {
            ("파일 탐색기", System.IO.Path.Combine(win, "explorer.exe")),
            ("Microsoft Edge", System.IO.Path.Combine(x86, @"Microsoft\Edge\Application\msedge.exe")),
            ("설정", AppsFolder + "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"),
            ("Microsoft Store", AppsFolder + "Microsoft.WindowsStore_8wekyb3d8bbwe!App"),
            ("터미널", AppsFolder + "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"),
            ("메모장", AppsFolder + "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"),
            ("계산기", AppsFolder + "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        })
            if (IconLoader.Get(path) != null) config.Pinned.Add(new() { Name = name, Path = path });
        config.Save();
        return config;
    }

    public void Save() =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
}
