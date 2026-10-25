using System.Text.Json;

namespace Mindows;

public class PinnedApp
{
    public string Name { get; set; } = "";
    /// <summary>Exe path, or shell:AppsFolder\&lt;AUMID&gt; for packaged apps.</summary>
    public string Path { get; set; } = "";
}

public class Config
{
    public const string AppsFolder = @"shell:AppsFolder\";
    public static readonly string Dir = Directory.CreateDirectory(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindows")).FullName;
    static string FilePath => System.IO.Path.Combine(Dir, "config.json");

    public List<PinnedApp> Pinned { get; set; } = new();

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new();
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
