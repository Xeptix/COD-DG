using CODDowngrader.Steam;

namespace CODDowngrader.Patching;

/// <param name="Locked">Files something has open, usually the game.</param>
/// <param name="Modified">Files that are not the base build's version: replaced by a mod or a client, or damaged. Files Steam personalizes are never counted.</param>
/// <param name="AlreadyThere">Files that are already the target build's version and need no writing.</param>
/// <param name="Replaced">How many existing files the patch replaces or removes.</param>
public sealed record PatchCheck(IReadOnlyList<string> Locked, IReadOnlyList<string> Modified, IReadOnlySet<string> AlreadyThere, int Replaced, ulong ReplacedBytes);

public sealed record UndoResult(int Restored, int Deleted, IReadOnlyList<string> Failed);

public enum DowngradeState
{
    Intact,

    /// <summary>Stopped while files were being written.</summary>
    Unfinished,

    /// <summary>Files the downgrade wrote have changed since, as Steam's Verify integrity does.</summary>
    FilesChanged,

    /// <summary>Steam has installed a different manifest of a depot in the folder since.</summary>
    SteamUpdated,
}

/// <summary>Writes a patch into an installed game, and takes it out again. Only files the patch names are ever touched.</summary>
public static class PatchApplier
{
    /// <summary>Where a build's file goes under a folder, or null for a name that would land outside it.</summary>
    public static string? PathIn(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var path = Path.GetFullPath(new ManifestFile(name, 0, 0).PathUnder(root));
            return PathRules.IsInside(path, root) && !PathRules.Same(path, root) ? path : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Reads every file of the game the plan replaces or removes. <paramref name="read"/> is told every block hashed.</summary>
    public static PatchCheck Check(string installDir, PatchPlan plan, Action<long>? read = null)
    {
        var locked = new List<string>();
        var modified = new List<string>();
        var there = new HashSet<string>(PathRules.Comparer);
        var replaced = 0;
        ulong bytes = 0;

        foreach (var write in plan.Writes)
        {
            if (PathIn(installDir, write.Name) is not { } path || !File.Exists(path)) continue;
            if (IsLocked(path))
            {
                locked.Add(write.Name);
                continue;
            }
            var sha = FileHash.Sha1(path, read);
            if (write.Sha.Length > 0 && sha == write.Sha)
            {
                there.Add(write.Name);
                continue;
            }
            if (write.BaseSha is not null && sha != write.BaseSha && !write.Personalized) modified.Add(write.Name);
            replaced++;
            bytes += (ulong)new FileInfo(path).Length;
        }

        foreach (var remove in plan.Removes)
        {
            if (PathIn(installDir, remove.Name) is not { } path || !File.Exists(path)) continue;
            if (IsLocked(path))
            {
                locked.Add(remove.Name);
                continue;
            }
            if (remove.Sha is not null && FileHash.Sha1(path, read) != remove.Sha) modified.Add(remove.Name);
            replaced++;
            bytes += (ulong)new FileInfo(path).Length;
        }

        return new PatchCheck(locked, modified, there, replaced, bytes);
    }

    /// <summary>The files of <paramref name="writes"/> that are missing from <paramref name="folder"/> or are not the build's version.</summary>
    public static List<PatchWrite> Missing(string folder, IEnumerable<PatchWrite> writes, Action<long>? read = null)
    {
        var bad = new List<PatchWrite>();
        foreach (var write in writes)
        {
            var path = PathIn(folder, write.Name);
            var info = path is null ? null : new FileInfo(path);
            if (info is null || !info.Exists || (ulong)info.Length != write.Size)
            {
                bad.Add(write);
                read?.Invoke((long)write.Size);
                continue;
            }
            if (write.Sha.Length > 0 && FileHash.Sha1(info.FullName, read) != write.Sha) bad.Add(write);
        }
        return bad;
    }

    /// <summary>
    /// Writes the plan into <see cref="AppliedRecord.InstallDir"/>: files from <paramref name="source"/> are moved or copied in,
    /// and what they replace, like the files the plan removes, goes to <see cref="AppliedRecord.Backup"/>, or is deleted when
    /// that is null. The record is saved before the first file is touched and after the last.
    /// </summary>
    public static void Apply(AppliedRecord record, PatchPlan plan, string source, bool move, IReadOnlySet<string> skip,
        Action<AppliedRecord> save, Action<long> progress)
    {
        var root = record.InstallDir;
        record.Written = plan.Writes
            .Where(w => !skip.Contains(w.Name) && PathIn(root, w.Name) is not null && PathIn(source, w.Name) is not null)
            .Select(w => new AppliedFile(w.Name, w.Size, File.Exists(PathIn(root, w.Name)), null))
            .ToList();
        record.Removed = plan.Removes.Where(r => File.Exists(PathIn(root, r.Name))).Select(r => r.Name).ToList();
        record.Complete = false;
        save(record);

        for (var i = 0; i < record.Written.Count; i++)
        {
            var file = record.Written[i];
            var target = PathIn(root, file.Name)!;
            var from = PathIn(source, file.Name)!;
            if (File.Exists(target)) SetAside(target, file.Name, record.Backup);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (move) File.Move(from, target);
            else File.Copy(from, target);
            record.Written[i] = file with { WrittenUtc = File.GetLastWriteTimeUtc(target) };
            progress((long)file.Size);
        }

        foreach (var name in record.Removed)
        {
            var target = PathIn(root, name)!;
            if (File.Exists(target)) SetAside(target, name, record.Backup);
        }

        record.Complete = true;
        save(record);
    }

    static void SetAside(string path, string name, string? backup)
    {
        if (backup is null)
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
            return;
        }
        var kept = PathIn(backup, name)!;
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.Move(path, kept, overwrite: true);
    }

    /// <param name="steamManifests">Steam's installed manifest of every depot in the folder now.</param>
    public static DowngradeState StateOf(AppliedRecord record, IReadOnlyDictionary<uint, ulong> steamManifests)
    {
        foreach (var (depot, manifest) in IdMap.Manifests(record.SteamManifests))
            if (steamManifests.TryGetValue(depot, out var now) && now != manifest) return DowngradeState.SteamUpdated;
        if (!record.Complete) return DowngradeState.Unfinished;
        return record.Written.All(f => Unchanged(record.InstallDir, f)) ? DowngradeState.Intact : DowngradeState.FilesChanged;
    }

    /// <summary>The file is still what the downgrade wrote, going by its size and last-write time.</summary>
    static bool Unchanged(string root, AppliedFile file)
    {
        if (PathIn(root, file.Name) is not { } path) return true;
        var info = new FileInfo(path);
        return info.Exists && (ulong)info.Length == file.Size && (file.WrittenUtc is null || info.LastWriteTimeUtc == file.WrittenUtc);
    }

    /// <summary>
    /// Takes a downgrade out of the game. Every file still as the downgrade wrote it gets its original back from the backup, or
    /// is deleted when the game had no file there; a file that has changed since is Steam's again and stays. Removed files come
    /// back from the backup unless Steam has put them back. The backup is deleted once nothing failed.
    /// </summary>
    public static UndoResult Undo(AppliedRecord record)
    {
        var root = record.InstallDir;
        var backup = record.Backup is not null && Directory.Exists(record.Backup) ? record.Backup : null;
        var restored = 0;
        var deleted = 0;
        var failed = new List<string>();

        foreach (var file in record.Written)
        {
            if (PathIn(root, file.Name) is not { } path) continue;
            try
            {
                if (File.Exists(path) && !Unchanged(root, file)) continue;
                var kept = backup is null ? null : PathIn(backup, file.Name);
                if (kept is not null && File.Exists(kept))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Move(kept, path, overwrite: true);
                    restored++;
                }
                else if (!file.HadOriginal && File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(file.Name);
            }
        }

        foreach (var name in record.Removed)
        {
            if (backup is null || PathIn(root, name) is not { } path || PathIn(backup, name) is not { } kept || !File.Exists(kept) || File.Exists(path)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Move(kept, path);
                restored++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(name);
            }
        }

        if (failed.Count == 0 && backup is not null)
        {
            try
            {
                Directory.Delete(backup, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(backup);
            }
        }
        return new UndoResult(restored, deleted, failed);
    }
}
