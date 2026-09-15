using CODDowngrader.Steam;

namespace CODDowngrader.Patching;

/// <summary>
/// A file Steam personalized for the account in the installed game, which a build has the same original of: <paramref name="Sha"/>
/// is the original's SHA-1 in both builds' manifests, <paramref name="InstalledSha"/> the installed copy's.
/// </summary>
public sealed record PersonalizedCopy(string Name, uint Depot, ulong Size, string Sha, string InstalledSha);

/// <summary>
/// Files Steam personalizes for each account when it installs a game (Custom Executable Generation). Steam only makes a
/// personalized copy of the build it installs, so one can go into another build only where both have the same original.
/// </summary>
public static class PersonalizedFiles
{
    /// <param name="build">The file lists of the build, by depot.</param>
    /// <param name="installed">The file lists of the build installed in <paramref name="installDir"/>, by depot.</param>
    public static List<PersonalizedCopy> Find(string installDir, IReadOnlyDictionary<uint, IReadOnlyList<ManifestFile>> build,
        IReadOnlyDictionary<uint, IReadOnlyList<ManifestFile>> installed)
    {
        var have = PatchPlan.Merge(installed);
        var copies = new List<PersonalizedCopy>();
        foreach (var (depot, file) in PatchPlan.Merge(build).Values)
        {
            if (file.Sha is null || !have.TryGetValue(file.Name, out var old) || old.File.Sha != file.Sha) continue;
            if (!file.IsCustomExecutable && !old.File.IsCustomExecutable) continue;
            if (PatchApplier.PathIn(installDir, file.Name) is not { } path || !File.Exists(path)) continue;
            try
            {
                if (!LooksPersonalized(path, file.Size)) continue;
                var sha = FileHash.Sha1(path);
                if (sha != file.Sha) copies.Add(new PersonalizedCopy(file.Name, depot, file.Size, file.Sha, sha));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return copies.OrderBy(c => c.Name, PathRules.Comparer).ToList();
    }

    /// <summary>
    /// Whether a file is Steam's personalization of an original of <paramref name="originalSize"/> bytes: an exe whose Authenticode
    /// signature starts where the original ends and runs to the end of the file, which is all the size Steam adds. An exe a mod or a
    /// client has replaced does not look like this.
    /// </summary>
    public static bool LooksPersonalized(string path, ulong originalSize)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return false;
        stream.Position = 0x3C;
        long pe = reader.ReadUInt32();
        if (pe + 26 > stream.Length) return false;
        stream.Position = pe;
        if (reader.ReadUInt32() != 0x00004550) return false;
        stream.Position = pe + 24;
        // The data directories follow the optional header's standard and Windows fields; the certificate table is the fifth.
        var directories = reader.ReadUInt16() switch
        {
            0x10B => pe + 24 + 96,
            0x20B => pe + 24 + 112,
            _ => 0L,
        };
        if (directories == 0 || directories + 5 * 8 > stream.Length) return false;
        stream.Position = directories + 4 * 8;
        var offset = reader.ReadUInt32();
        var size = reader.ReadUInt32();
        return offset == originalSize && (long)offset + size == stream.Length;
    }
}
