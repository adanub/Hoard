namespace Hoard.Core.Projects;

/// <summary>
/// App-level (not project-level) locations: the small settings file that remembers recent projects,
/// and a global diagnostic log. Gallery data never lives here — that's per-project (see
/// <see cref="HoardProject"/>).
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string appDataRoot)
    {
        AppDataRoot = appDataRoot;
        LogsRoot = Path.Combine(appDataRoot, "logs");
        SettingsFile = Path.Combine(appDataRoot, "settings.json");
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    public string AppDataRoot { get; }
    public string LogsRoot { get; }
    public string SettingsFile { get; }

    /// <summary>Default per-user app data directory, e.g. %APPDATA%/Hoard.</summary>
    public static AppPaths Default()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hoard"));
}
