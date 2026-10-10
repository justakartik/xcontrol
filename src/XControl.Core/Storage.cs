using System.Text;
using System.Text.Json;

namespace XControl.Core;

public sealed class AppSettings
{
    public string ActiveProfile { get; set; } = DefaultProfiles.ForzaName;
    /// <summary>Global toggle shortcut, e.g. "Ctrl+Shift+F8".</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+F8";
    /// <summary>Only translate input while one of <see cref="BrowserProcesses"/> is the foreground window.</summary>
    public bool OnlyWhenBrowserFocused { get; set; } = true;
    public List<string> BrowserProcesses { get; set; } = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi"];
    public int UpdateRateHz { get; set; } = 125;
    public string LogLevel { get; set; } = "Info";
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool RunAtStartup { get; set; }
    public bool EnableOnLaunch { get; set; }

    public void Clamp()
    {
        UpdateRateHz = Math.Clamp(UpdateRateHz, 30, 500);
        if (BrowserProcesses is not { Count: > 0 }) BrowserProcesses = ["chrome", "msedge", "firefox"];
        if (string.IsNullOrWhiteSpace(ToggleHotkey)) ToggleHotkey = "Ctrl+Shift+F8";
    }
}

/// <summary>Profiles and settings in a folder (default %APPDATA%\XControl). Corrupt files are quarantined, never fatal.</summary>
public sealed class ProfileStore
{
    public string Root { get; }
    string ProfilesDir => Path.Combine(Root, "profiles");
    string SettingsPath => Path.Combine(Root, "settings.json");
    public List<string> Warnings { get; } = [];

    public ProfileStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XControl");
        Directory.CreateDirectory(ProfilesDir);
    }

    public static string FileSafe(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name) sb.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "profile" : s;
    }

    string PathFor(string name) => Path.Combine(ProfilesDir, FileSafe(name) + ".json");

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Profile.Json);
                if (s != null) { s.Clamp(); return s; }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Quarantine(SettingsPath, "settings.json unreadable: " + ex.Message);
        }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings s) => AtomicWrite(SettingsPath, JsonSerializer.Serialize(s, Profile.Json));

    /// <summary>Loads every profile, ensuring the built-in ones exist (re-created if deleted or corrupt).</summary>
    public List<Profile> LoadProfiles()
    {
        var list = new List<Profile>();
        foreach (var file in Directory.EnumerateFiles(ProfilesDir, "*.json"))
        {
            if (TryRead(file, out var p, out var err)) list.Add(p!);
            else Quarantine(file, err!);
        }
        foreach (var def in DefaultProfiles.All())
            if (!list.Any(p => p.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(def);
                Save(def);
            }
        return list.OrderBy(p => !p.BuiltIn).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool TryRead(string file, out Profile? profile, out string? error)
    {
        profile = null; error = null;
        try
        {
            var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(file), Profile.Json);
            if (p == null) { error = "empty profile"; return false; }
            var problems = p.Validate();
            foreach (var pr in problems) Warnings.Add($"{Path.GetFileName(file)}: {pr}");
            profile = p;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Save(Profile p) => AtomicWrite(PathFor(p.Name), JsonSerializer.Serialize(p, Profile.Json));

    public void Delete(string name)
    {
        var f = PathFor(name);
        if (File.Exists(f)) File.Delete(f);
    }

    public Profile Import(string file)
    {
        if (!TryRead(file, out var p, out var err)) throw new InvalidDataException("Not a valid profile: " + err);
        p!.BuiltIn = false;
        var baseName = p.Name;
        for (var i = 2; File.Exists(PathFor(p.Name)); i++) p.Name = $"{baseName} ({i})";
        Save(p);
        return p;
    }

    public void Export(Profile p, string file) => File.WriteAllText(file, JsonSerializer.Serialize(p, Profile.Json));

    void Quarantine(string file, string reason)
    {
        Warnings.Add($"{Path.GetFileName(file)} was unreadable ({reason}); it was renamed to .bad and defaults are used.");
        try { File.Move(file, file + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bad", true); }
        catch (IOException) { }
    }

    static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, true);
    }
}
