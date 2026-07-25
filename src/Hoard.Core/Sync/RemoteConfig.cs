using System.Text.Json;
using Hoard.Core.Projects;

namespace Hoard.Core.Sync;

/// <summary>
/// This machine's remote for one project (P5/R2): where the archive replicates to/from. Lives in
/// app-data project state (<see cref="AppPaths.ProjectRemoteConfigPath"/>), NOT in the archive folder —
/// a remote is a machine's relationship to the archive, and another machine may reach the same remote
/// by a different path, or have none. One remote per project for now.
/// </summary>
public sealed record RemoteConfig(string Type, string Target)
{
    /// <summary>A mounted-folder remote (<see cref="FileSystemRemoteStore"/>): Target is its path.</summary>
    public const string FolderType = "folder";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>The stored remote for a project, or null when none is configured (or unreadable).</summary>
    public static RemoteConfig? Load(AppPaths appPaths, Guid projectId)
    {
        try
        {
            var path = appPaths.ProjectRemoteConfigPath(projectId);
            if (!File.Exists(path)) return null;
            var config = JsonSerializer.Deserialize<RemoteConfig>(File.ReadAllText(path), Json);
            return string.IsNullOrWhiteSpace(config?.Type) || string.IsNullOrWhiteSpace(config.Target) ? null : config;
        }
        catch
        {
            return null; // a garbled config reads as "none" — the user re-picks, nothing is lost
        }
    }

    public void Save(AppPaths appPaths, Guid projectId)
    {
        var path = appPaths.ProjectRemoteConfigPath(projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static void Remove(AppPaths appPaths, Guid projectId)
    {
        try
        {
            var path = appPaths.ProjectRemoteConfigPath(projectId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* nothing depends on the file being gone */ }
    }

    /// <summary>The live store this config describes.</summary>
    public IRemoteStore CreateStore() => Type switch
    {
        FolderType => new FileSystemRemoteStore(Target),
        _ => throw new NotSupportedException($"Unknown remote type '{Type}' — configured by a newer version?"),
    };
}
