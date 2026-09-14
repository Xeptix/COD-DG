using CODDowngrader.App;
using CODDowngrader.Steam;

namespace CODDowngrader.Download;

public sealed record SeedFile(uint Depot, string Name, string Source, string Target, long Bytes);

public sealed record CleanupResult(int Removed, uint? UnreadableDepot);

/// <summary>
/// Starts a download from the installed game. Files the chosen build shares with the install are
/// copied into the new folder first; DepotDownloader then checks each file already there chunk by
/// chunk and fetches only what differs. An update that only changed the exe costs the exe.
/// </summary>
public static class Seeder
{
    /// <summary>
    /// What to copy for one depot. With both file lists cached, only files whose content hash matches
    /// are copied: a changed file shares no chunk DepotDownloader could reuse. With only the chosen
    /// build's list, every file it names is copied and DepotDownloader corrects them. With only the
    /// installed list, the installed files are copied and the ones the chosen build lacks are removed
    /// once the download has finished.
    /// </summary>
    public static List<SeedFile> Plan(string installDir, string destination, uint depot,
        IReadOnlyList<ManifestFile>? chosen, IReadOnlyList<ManifestFile>? installed, bool sameManifest)
    {
        IEnumerable<ManifestFile> candidates;
        if (chosen is not null && installed is not null && !sameManifest)
        {
            var installedSha = installed
                .Where(f => f.Sha is not null)
                .GroupBy(f => f.Name, PathRules.Comparer)
                .ToDictionary(g => g.Key, g => g.First().Sha, PathRules.Comparer);
            candidates = chosen.Where(f => f.Sha is not null && installedSha.TryGetValue(f.Name, out var sha) && sha == f.Sha);
        }
        else
        {
            candidates = chosen ?? installed ?? Array.Empty<ManifestFile>();
        }

        var plan = new List<SeedFile>();
        foreach (var file in candidates.Where(f => !f.IsDirectory && !f.IsSymlink && f.Size > 0).DistinctBy(f => f.Name, PathRules.Comparer))
        {
            var source = file.PathUnder(installDir);
            var target = Path.GetFullPath(file.PathUnder(destination));
            if (!PathRules.IsInside(target, destination) || File.Exists(target)) continue;
            try
            {
                var info = new FileInfo(source);
                if (info.Exists) plan.Add(new SeedFile(depot, file.Name, source, target, info.Length));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return plan;
    }

    public static void Copy(IReadOnlyList<SeedFile> plan, Action<long, long> progress, CancellationToken ct)
    {
        var total = plan.Sum(p => p.Bytes);
        long done = 0;
        foreach (var file in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(file.Target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
            File.Copy(file.Source, file.Target);
            done += file.Bytes;
            progress(done, total);
        }
    }

    /// <summary>
    /// Deletes files copied for <paramref name="part"/> that no finished depot in the folder contains:
    /// files from the installed build the chosen build does not have, and files of depots the account
    /// could not download. Only copies named in the record are ever touched. Nothing is deleted when a
    /// finished depot's saved manifest cannot be read, because then its files are not known.
    /// </summary>
    public static CleanupResult RemoveExtras(string destination, DownloadRecord record, DownloadPart part, IReadOnlyDictionary<uint, ulong> finished)
    {
        if (part.CopiedFromInstall.Count == 0) return new CleanupResult(0, null);

        var keep = new HashSet<string>(PathRules.Comparer);
        foreach (var (depot, manifest) in record.Downloads.SelectMany(d => d.ManifestMap()))
        {
            if (!finished.TryGetValue(depot, out var done) || done != manifest) continue;
            var files = DepotManifest.TryLoadFiles(Path.Combine(destination, ".DepotDownloader", $"{depot}_{manifest}.manifest"));
            if (files is null) return new CleanupResult(0, depot);
            keep.UnionWith(files.Select(f => f.Name));
        }

        var removed = 0;
        foreach (var name in part.CopiedFromInstall.Values.SelectMany(n => n).Distinct(PathRules.Comparer))
        {
            if (keep.Contains(name)) continue;
            var path = new ManifestFile(name, 0, 0).PathUnder(destination);
            if (!PathRules.IsInside(path, destination) || !File.Exists(path)) continue;
            File.Delete(path);
            removed++;
        }
        part.CopiedFromInstall.Clear();
        return new CleanupResult(removed, null);
    }
}
