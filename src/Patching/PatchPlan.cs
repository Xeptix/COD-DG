using System.Security.Cryptography;
using CODDowngrader.Steam;

namespace CODDowngrader.Patching;

/// <summary>
/// A file a patch writes: the target build's version, from one depot. <paramref name="BaseSha"/> is the base build's version,
/// when it has one. <paramref name="Personalized"/> is a file Steam personalizes for each account in either build, so the
/// installed copy's SHA-1 says nothing about which build it is.
/// </summary>
public sealed record PatchWrite(string Name, uint Depot, ulong Size, string Sha, string? BaseSha, bool Personalized = false);

/// <summary>A file of the base build that no depot of the target build has.</summary>
public sealed record PatchRemove(string Name, ulong Size, string? Sha);

/// <summary>
/// The files that turn one build into another: every file of a changed depot whose content differs, and the
/// base build's files that no depot of the target build has. Everything else in the folder stays as it is.
/// </summary>
public sealed class PatchPlan
{
    public PatchPlan(IReadOnlyList<PatchWrite> writes, IReadOnlyList<PatchRemove> removes, bool removesKnown)
    {
        // Patches saved by 1.0.0 can name one file two ways, "./main/file" to write and "main/file" to remove.
        Writes = writes.Select(w => w with { Name = ManifestFile.NormalizeName(w.Name) }).ToList();
        var written = new HashSet<string>(Writes.Select(w => w.Name), PathRules.Comparer);
        Removes = removes.Select(r => r with { Name = ManifestFile.NormalizeName(r.Name) }).Where(r => !written.Contains(r.Name)).ToList();
        RemovesKnown = removesKnown;
    }

    public IReadOnlyList<PatchWrite> Writes { get; }

    public IReadOnlyList<PatchRemove> Removes { get; }

    /// <summary>False when a depot that stays as it is had no readable file list, so nothing is removed: a file could be that depot's.</summary>
    public bool RemovesKnown { get; }

    public ulong WriteBytes => Writes.Aggregate(0UL, (sum, w) => sum + w.Size);

    /// <param name="baseLists">The base build's file list of each changed depot. A depot the base build does not have is left out.</param>
    /// <param name="targetLists">The target build's file list of each changed depot.</param>
    /// <param name="keptLists">The file lists of the folder's depots that stay as they are; null for one that could not be read.</param>
    public static PatchPlan Compute(IReadOnlyDictionary<uint, IReadOnlyList<ManifestFile>> baseLists,
        IReadOnlyDictionary<uint, IReadOnlyList<ManifestFile>> targetLists, IReadOnlyCollection<IReadOnlyList<ManifestFile>?> keptLists)
    {
        var before = Merge(baseLists);
        var after = Merge(targetLists);

        var writes = new List<PatchWrite>();
        foreach (var (depot, file) in after.Values)
        {
            var old = before.TryGetValue(file.Name, out var b) ? b.File : null;
            if (old?.Sha is not null && old.Sha == file.Sha) continue;
            writes.Add(new PatchWrite(file.Name, depot, file.Size, file.Sha ?? "", old?.Sha, file.IsCustomExecutable || old?.IsCustomExecutable == true));
        }

        var removesKnown = keptLists.All(l => l is not null);
        var removes = new List<PatchRemove>();
        if (removesKnown)
        {
            var kept = new HashSet<string>(keptLists.SelectMany(l => l!).Select(f => f.Name), PathRules.Comparer);
            foreach (var (_, file) in before.Values)
            {
                if (!after.ContainsKey(file.Name) && !kept.Contains(file.Name))
                    removes.Add(new PatchRemove(file.Name, file.Size, file.Sha));
            }
        }

        return new PatchPlan(
            writes.OrderBy(w => w.Name, PathRules.Comparer).ToList(),
            removes.OrderBy(r => r.Name, PathRules.Comparer).ToList(),
            removesKnown);
    }

    /// <summary>Every file of the lists by path. Where two depots have the same file, the higher depot's is the one kept.</summary>
    static Dictionary<string, (uint Depot, ManifestFile File)> Merge(IReadOnlyDictionary<uint, IReadOnlyList<ManifestFile>> lists)
    {
        var merged = new Dictionary<string, (uint, ManifestFile)>(PathRules.Comparer);
        foreach (var (depot, files) in lists.OrderBy(l => l.Key))
        {
            foreach (var file in files)
            {
                var name = ManifestFile.NormalizeName(file.Name);
                if (!file.IsDirectory && !file.IsSymlink && name.Length > 0) merged[name] = (depot, file with { Name = name });
            }
        }
        return merged;
    }
}

public static class FileHash
{
    /// <summary>A file's SHA-1 as Steam's manifests write it: upper-case hex. <paramref name="read"/> is told every block read.</summary>
    public static string Sha1(string path, Action<long>? read = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1 << 20];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, count);
            read?.Invoke(count);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
