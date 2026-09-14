using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace CODDowngrader.Steam;

public sealed record ManifestFile(string Name, ulong Size, uint Flags, string? Sha = null)
{
    public bool IsDirectory => (Flags & 0x40) != 0;

    public bool IsSymlink => (Flags & 0x200) != 0;

    /// <summary>The file's path under an install folder, using this OS's separator.</summary>
    public string PathUnder(string root) => Path.Combine(root, Name.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>
/// A Steam content manifest: Steam's own depotcache\{depot}_{manifest}.manifest, or the copy
/// DepotDownloader saves beside a download. Sections are a payload (the file list), metadata,
/// and a signature, each as magic + length + protobuf, then an end marker.
/// </summary>
public sealed class DepotManifest
{
    const uint PayloadMagic = 0x71F617D0;
    const uint MetadataMagic = 0x1F4812BE;
    const uint SignatureMagic = 0x1B81B817;
    const uint EndMagic = 0x32C415AB;

    public uint DepotId { get; private init; }
    public ulong ManifestId { get; private init; }
    public DateTimeOffset Created { get; private init; }
    public bool FilenamesEncrypted { get; private init; }
    public ulong SizeOnDisk { get; private init; }

    /// <summary>Empty unless the file list was requested and the names are readable.</summary>
    public IReadOnlyList<ManifestFile> Files { get; private init; } = Array.Empty<ManifestFile>();

    public static DepotManifest Load(string path, bool withFiles = true) =>
        Parse(File.ReadAllBytes(path), withFiles);

    /// <summary>The readable file list of a manifest on disk, or null when it is missing, damaged or encrypted.</summary>
    public static IReadOnlyList<ManifestFile>? TryLoadFiles(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var parsed = Load(path);
            return parsed.FilenamesEncrypted ? null : parsed.Files;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Metadata only. The file list comes first and is most of the file for a big depot, so it is skipped.</summary>
    public static DepotManifest ReadHeader(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var header = new byte[8];
            while (stream.Length - stream.Position >= 8)
            {
                stream.ReadExactly(header);
                if (header[0] == 'P' && header[1] == 'K') break;

                var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
                long length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                if (length > stream.Length - stream.Position) break;

                if (magic == MetadataMagic)
                {
                    var section = new byte[8 + length];
                    header.CopyTo(section, 0);
                    stream.ReadExactly(section, 8, (int)length);
                    return Parse(section, withFiles: false);
                }
                if (magic is not (PayloadMagic or SignatureMagic)) break;
                stream.Position += length;
            }
        }

        return Load(path, withFiles: false);
    }

    public static DepotManifest Parse(byte[] data, bool withFiles = true)
    {
        if (data.Length >= 2 && data[0] == 'P' && data[1] == 'K') data = Unzip(data);

        ReadOnlySpan<byte> payload = default;
        ReadOnlySpan<byte> metadata = default;
        var sawMetadata = false;
        var pos = 0;

        while (data.Length - pos >= 4)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            if (magic == EndMagic) break;
            if (magic is not (PayloadMagic or MetadataMagic or SignatureMagic))
                throw new InvalidDataException($"Not a Steam manifest (section 0x{magic:X8}).");
            if (data.Length - pos < 8) throw new InvalidDataException("Truncated manifest section.");

            long length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            if (length > data.Length - pos - 8) throw new InvalidDataException("Truncated manifest section.");

            var body = data.AsSpan(pos + 8, (int)length);
            if (magic == PayloadMagic) payload = body;
            else if (magic == MetadataMagic)
            {
                metadata = body;
                sawMetadata = true;
            }
            pos += 8 + (int)length;
        }

        if (!sawMetadata) throw new InvalidDataException("Manifest has no metadata section.");

        uint depot = 0, created = 0;
        ulong gid = 0, size = 0;
        var encrypted = false;

        var meta = new ProtoReader(metadata);
        while (meta.Next())
        {
            if (!meta.IsVarint)
            {
                meta.Skip();
                continue;
            }

            var value = meta.ReadVarint();
            switch (meta.FieldNumber)
            {
                case 1: depot = (uint)value; break;
                case 2: gid = value; break;
                case 3: created = (uint)value; break;
                case 4: encrypted = value != 0; break;
                case 5: size = value; break;
            }
        }

        var files = withFiles && !encrypted ? ReadFiles(payload) : new List<ManifestFile>();

        return new DepotManifest
        {
            DepotId = depot,
            ManifestId = gid,
            Created = DateTimeOffset.FromUnixTimeSeconds(created),
            FilenamesEncrypted = encrypted,
            SizeOnDisk = size,
            Files = files,
        };
    }

    static List<ManifestFile> ReadFiles(ReadOnlySpan<byte> payload)
    {
        var files = new List<ManifestFile>();
        var reader = new ProtoReader(payload);

        while (reader.Next())
        {
            if (reader.FieldNumber != 1 || !reader.IsBytes)
            {
                reader.Skip();
                continue;
            }

            var mapping = new ProtoReader(reader.ReadBytes());
            var name = "";
            ulong size = 0;
            uint flags = 0;
            string? sha = null;

            while (mapping.Next())
            {
                if (mapping.FieldNumber == 1 && mapping.IsBytes) name = Encoding.UTF8.GetString(mapping.ReadBytes());
                else if (mapping.FieldNumber == 2 && mapping.IsVarint) size = mapping.ReadVarint();
                else if (mapping.FieldNumber == 3 && mapping.IsVarint) flags = (uint)mapping.ReadVarint();
                else if (mapping.FieldNumber == 5 && mapping.IsBytes) sha = Convert.ToHexString(mapping.ReadBytes());
                else mapping.Skip();
            }

            files.Add(new ManifestFile(name.Replace('\\', '/').TrimEnd('/'), size, flags, sha is { Length: > 0 } ? sha : null));
        }

        return files;
    }

    static byte[] Unzip(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault() ?? throw new InvalidDataException("Empty manifest archive.");
            using var stream = entry.Open();
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception e) when (e is not InvalidDataException and (IOException or NotSupportedException or ArgumentException))
        {
            throw new InvalidDataException("Damaged manifest archive.", e);
        }
    }
}
