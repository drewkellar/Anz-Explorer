namespace AnzExplorer.Core;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        DataRoot = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Anz Explorer");
    }

    public string DataRoot { get; }
    public string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public string SettingsBackupPath => Path.Combine(DataRoot, "settings.last-good.json");
    public string IndexPath => Path.Combine(DataRoot, "folder-index.db");
    public string LogDirectory => Path.Combine(DataRoot, "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogDirectory);
    }
}

