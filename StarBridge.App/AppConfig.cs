namespace StarBridge.App;

internal sealed record AppConfig(
    string? LogPath,
    string? LastPlayerName,
    string? DisplayName,
    string? AvatarPath,
    string? LastPlayerId)
{
    public static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StarBridge");

    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "app.config");

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new AppConfig(
                LogPath: null,
                LastPlayerName: null,
                DisplayName: null,
                AvatarPath: null,
                LastPlayerId: null);
        }

        var lines = File.ReadAllLines(ConfigPath);

        return new AppConfig(
            lines.Length > 0 ? lines[0] : null,
            lines.Length > 1 ? lines[1] : null,
            lines.Length > 2 ? lines[2] : null,
            lines.Length > 3 ? lines[3] : null,
            lines.Length > 4 ? lines[4] : null);
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory); 
        File.WriteAllLines(
            ConfigPath,
            [
                config.LogPath ?? "",
                config.LastPlayerName ?? "",
                config.DisplayName ?? "",
                config.AvatarPath ?? "",
                config.LastPlayerId ?? ""
            ]);






    }
}
