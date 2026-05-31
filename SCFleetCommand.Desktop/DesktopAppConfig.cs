namespace SCFleetCommand.Desktop;

using System.IO;

internal sealed record DesktopAppConfig(
    string? LogPath,
    string? PlayerName,
    string? PlayerId,
    string? AvatarPath,
    string? OverlayHotkey,
    string? OverlayLayout,
    string? Callsign,
    string? OverlaySettings,
    string? Language)
{
    public static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SCFleetCommand");

    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "desktop.config");
    private static readonly string OverlaySettingsPath = Path.Combine(ConfigDirectory, "overlay.settings");
    private static readonly string OverlayLayoutPath = Path.Combine(ConfigDirectory, "overlay.layout");
    private static readonly string FallbackConfigDirectory = Path.Combine(AppContext.BaseDirectory, "config");
    private static readonly string FallbackConfigPath = Path.Combine(FallbackConfigDirectory, "desktop.config");
    private static readonly string FallbackOverlaySettingsPath = Path.Combine(FallbackConfigDirectory, "overlay.settings");
    private static readonly string FallbackOverlayLayoutPath = Path.Combine(FallbackConfigDirectory, "overlay.layout");

    public static DesktopAppConfig Load()
    {
        var path = File.Exists(ConfigPath)
            ? ConfigPath
            : FallbackConfigPath;

        if (!File.Exists(path))
        {
            return new DesktopAppConfig(null, null, null, null, null, null, null, null, null);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return new DesktopAppConfig(null, null, null, null, null, null, null, null, null);
        }

        return new DesktopAppConfig(
            lines.Length > 0 ? EmptyToNull(lines[0]) : null,
            lines.Length > 1 ? EmptyToNull(lines[1]) : null,
            lines.Length > 2 ? EmptyToNull(lines[2]) : null,
            lines.Length > 3 ? EmptyToNull(lines[3]) : null,
            lines.Length > 4 ? EmptyToNull(lines[4]) : null,
            lines.Length > 5 ? EmptyToNull(lines[5]) : null,
            lines.Length > 6 ? EmptyToNull(lines[6]) : null,
            lines.Length > 7 ? EmptyToNull(lines[7]) : null,
            lines.Length > 8 ? EmptyToNull(lines[8]) : null);
    }

    public static void Save(DesktopAppConfig config)
    {
        var lines = new[]
        {
            config.LogPath ?? "",
            config.PlayerName ?? "",
            config.PlayerId ?? "",
            config.AvatarPath ?? "",
            config.OverlayHotkey ?? "",
            config.OverlayLayout ?? "",
            config.Callsign ?? "",
            config.OverlaySettings ?? "",
            config.Language ?? ""
        };

        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllLines(ConfigPath, lines);
            return;
        }
        catch
        {
            Directory.CreateDirectory(FallbackConfigDirectory);
            File.WriteAllLines(FallbackConfigPath, lines);
        }
    }

    public static string? LoadOverlaySettings()
    {
        return ReadOptionalText(OverlaySettingsPath) ?? ReadOptionalText(FallbackOverlaySettingsPath);
    }

    public static void SaveOverlaySettings(string value)
    {
        WriteTextWithFallback(OverlaySettingsPath, FallbackOverlaySettingsPath, value);
    }

    public static string? LoadOverlayLayout()
    {
        return ReadOptionalText(OverlayLayoutPath) ?? ReadOptionalText(FallbackOverlayLayoutPath);
    }

    public static void SaveOverlayLayout(string value)
    {
        WriteTextWithFallback(OverlayLayoutPath, FallbackOverlayLayoutPath, value);
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ReadOptionalText(string path)
    {
        try
        {
            return File.Exists(path) ? EmptyToNull(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteTextWithFallback(string path, string fallbackPath, string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
            return;
        }
        catch
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
            File.WriteAllText(fallbackPath, value);
        }
    }
}
