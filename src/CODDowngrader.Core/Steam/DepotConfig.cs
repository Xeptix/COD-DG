using System.IO.Compression;

namespace CODDowngrader.Steam;

/// <summary>
/// DepotDownloader's own record of a download folder: .DepotDownloader\depot.config, a DeflateStream
/// around protobuf-net's DepotConfigStore { [1] Dictionary&lt;uint, ulong&gt; InstalledManifestIDs }.
/// DepotDownloader marks a depot ulong.MaxValue when it starts on it and writes the manifest ID only
/// once every file of that depot is in place, so this is the authority on what actually finished.
/// </summary>
public static class DepotConfig
{
    public const ulong InProgress = ulong.MaxValue;

    public static string PathIn(string downloadFolder) => Path.Combine(downloadFolder, ".DepotDownloader", "depot.config");

    /// <summary>Depot to manifest for the folder, or null when there is no readable depot.config.</summary>
    public static Dictionary<uint, ulong>? Read(string downloadFolder)
    {
        var path = PathIn(downloadFolder);
        if (!File.Exists(path)) return null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var deflate = new DeflateStream(file, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            deflate.CopyTo(buffer);

            // DepotDownloader records a depot before it touches any file, so a real depot.config is never empty.
            var map = Parse(buffer.ToArray());
            return map.Count > 0 ? map : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Dictionary<uint, ulong> Parse(ReadOnlySpan<byte> protobuf)
    {
        var result = new Dictionary<uint, ulong>();
        var reader = new ProtoReader(protobuf);
        while (reader.Next())
        {
            if (reader.FieldNumber != 1 || !reader.IsBytes)
            {
                reader.Skip();
                continue;
            }

            var entry = new ProtoReader(reader.ReadBytes());
            uint depot = 0;
            ulong manifest = 0;
            while (entry.Next())
            {
                if (entry.FieldNumber == 1 && entry.IsVarint) depot = (uint)entry.ReadVarint();
                else if (entry.FieldNumber == 2 && entry.IsVarint) manifest = entry.ReadVarint();
                else entry.Skip();
            }
            result[depot] = manifest;
        }
        return result;
    }
}
