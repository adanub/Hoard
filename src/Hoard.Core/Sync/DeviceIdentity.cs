using Hoard.Core.Projects;

namespace Hoard.Core.Sync;

/// <summary>
/// The stable per-install device id that names this machine's append-only op segment (see
/// <c>SYNC-DESIGN.md</c>: one writer per segment file is the whole concurrency model, so the id must
/// never be shared between machines). Minted once and kept in its own small file under app data — not in
/// <c>settings.json</c>, which <see cref="ProjectManager"/> rewrites whole.
/// </summary>
public static class DeviceIdentity
{
    private const string FileName = "device-id";

    public static string GetOrCreate(AppPaths appPaths)
    {
        var path = Path.Combine(appPaths.AppDataRoot, FileName);
        try
        {
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing))
                return existing.ToString("N");
        }
        catch { /* unreadable — mint a fresh id below */ }

        var id = Guid.NewGuid();
        File.WriteAllText(path, id.ToString("N"));
        return id.ToString("N");
    }
}
