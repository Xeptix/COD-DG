using CODDowngrader.Steam;

namespace CODDowngrader.Patching;

/// <summary>A file an undo takes from the backup, and its size there.</summary>
public sealed record UndoFile(string Name, ulong Size);

/// <summary>
/// What taking a downgrade out of a game does with each file it wrote or removed. The original comes back from the backup; where
/// no backup holds it, it is downloaded from Steam at the build Steam has installed, which is the downgrade's patch run the other
/// way. A file the downgrade added is deleted, and a file Steam has replaced since is Steam's again and stays.
/// </summary>
/// <param name="Lost">Files neither the backup nor Steam's file lists have: they stay as the downgrade left them.</param>
public sealed record UndoPlan(
    IReadOnlyList<UndoFile> FromBackup,
    IReadOnlyList<PatchWrite> FromSteam,
    IReadOnlyList<string> Delete,
    IReadOnlyList<string> SteamChanged,
    IReadOnlyList<string> Lost)
{
    public ulong DownloadBytes => FromSteam.Aggregate(0UL, (sum, w) => sum + w.Size);

    /// <summary>
    /// The depots whose file lists say where a file without a backup comes from: the ones the downgrade changed, and those of
    /// Steam's language that a language written in stood in for.
    /// </summary>
    public static IEnumerable<uint> Depots(AppliedRecord record) =>
        IdMap.Manifests(record.TargetManifests).Keys.Concat(IdMap.Owners(record.LanguageSwaps).Values);

    /// <param name="installed">Steam's file list of each depot at the build it has installed. A depot without one is not searched.</param>
    public static UndoPlan Compute(AppliedRecord record, IReadOnlyDictionary<uint, IReadOnlyList<ManifestFile>> installed)
    {
        var root = record.InstallDir;
        var backup = record.Backup is not null && Directory.Exists(record.Backup) ? record.Backup : null;

        // Where two depots have a file of the same name, the last one written wins on disk, as DepotDownloader writes them.
        var steam = new Dictionary<string, (uint Depot, ManifestFile File)>(PathRules.Comparer);
        foreach (var (depot, files) in installed.OrderBy(i => i.Key))
            foreach (var file in files)
                steam[ManifestFile.NormalizeName(file.Name)] = (depot, file);

        var fromBackup = new List<UndoFile>();
        var fromSteam = new List<PatchWrite>();
        var delete = new List<string>();
        var changed = new List<string>();
        var lost = new List<string>();

        UndoFile? Kept(string name) =>
            backup is not null && PatchApplier.PathIn(backup, name) is { } kept && new FileInfo(kept) is { Exists: true } info
                ? new UndoFile(name, (ulong)info.Length)
                : null;

        PatchWrite? Steam(string name) =>
            steam.TryGetValue(ManifestFile.NormalizeName(name), out var found)
                ? new PatchWrite(name, found.Depot, found.File.Size, found.File.Sha ?? "", null, found.File.IsCustomExecutable)
                : null;

        foreach (var file in record.Written)
        {
            if (PatchApplier.PathIn(root, file.Name) is not { } path) continue;
            if (File.Exists(path) && !PatchApplier.Unchanged(root, file)) changed.Add(file.Name);
            else if (Kept(file.Name) is { } kept) fromBackup.Add(kept);
            else if (!file.HadOriginal)
            {
                if (File.Exists(path)) delete.Add(file.Name);
            }
            else if (Steam(file.Name) is { } original) fromSteam.Add(original);
            else lost.Add(file.Name);
        }

        foreach (var name in record.Removed)
        {
            if (PatchApplier.PathIn(root, name) is not { } path || File.Exists(path)) continue;
            if (Kept(name) is { } kept) fromBackup.Add(kept);
            else if (Steam(name) is { } original) fromSteam.Add(original);
            else lost.Add(name);
        }

        return new UndoPlan(fromBackup, fromSteam, delete, changed, lost);
    }
}
