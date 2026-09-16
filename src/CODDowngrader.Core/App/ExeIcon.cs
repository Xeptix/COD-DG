namespace CODDowngrader.App;

/// <summary>
/// The icon inside a Windows exe, read from its resources by hand so it works anywhere: the exe's first icon group, as a
/// one-image .ico holding the smallest image at least as big as the one asked for, or the biggest there is. Only the headers,
/// the resource tree and that one image are read, so a 100 MB exe costs a few kilobytes.
/// </summary>
public static class ExeIcon
{
    const uint RtIcon = 3;
    const uint RtGroupIcon = 14;
    const uint Subdirectory = 0x80000000;

    /// <summary>The icon as .ico bytes; null when the file is not an exe, has no icon, or cannot be read.</summary>
    public static byte[]? Read(string path, int wanted = 48)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            return Read(stream, reader, wanted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    static byte[]? Read(Stream stream, BinaryReader reader, int wanted)
    {
        // The DOS header points at the PE header; the optional header's third data directory is the resource tree.
        if (stream.Length < 0x40) return null;
        if (reader.ReadUInt16() != 0x5A4D) return null;
        stream.Position = 0x3C;
        long pe = reader.ReadUInt32();
        if (pe + 24 > stream.Length) return null;
        stream.Position = pe;
        if (reader.ReadUInt32() != 0x00004550) return null;
        stream.Position = pe + 6;
        var sectionCount = reader.ReadUInt16();
        stream.Position = pe + 20;
        var optionalSize = reader.ReadUInt16();
        var optional = pe + 24;
        stream.Position = optional;
        var directories = optional + (reader.ReadUInt16() == 0x20B ? 112 : 96);
        if (directories + 24 > optional + optionalSize || optional + optionalSize > stream.Length) return null;
        stream.Position = directories + 16;
        var resourceRva = reader.ReadUInt32();
        if (resourceRva == 0) return null;

        var sections = new List<(uint Va, uint Size, uint Raw, uint RawSize)>();
        for (var i = 0; i < sectionCount && i < 96; i++)
        {
            var header = optional + optionalSize + i * 40L;
            if (header + 40 > stream.Length) return null;
            stream.Position = header + 8;
            var virtualSize = reader.ReadUInt32();
            var va = reader.ReadUInt32();
            var rawSize = reader.ReadUInt32();
            var raw = reader.ReadUInt32();
            sections.Add((va, Math.Max(virtualSize, rawSize), raw, rawSize));
        }

        long? FileOffset(uint rva)
        {
            foreach (var (va, size, raw, rawSize) in sections)
                if (rva >= va && rva - va < size && rva - va < rawSize) return (long)raw + (rva - va);
            return null;
        }

        if (FileOffset(resourceRva) is not { } root) return null;

        // Each level of the tree (type, then name, then language) is a directory of entries: an ID or name, and an offset from
        // the start of the tree to a subdirectory (high bit set) or to the data.
        List<(uint Id, uint Offset)> Entries(uint offset)
        {
            var list = new List<(uint Id, uint Offset)>();
            var directory = root + (offset & ~Subdirectory);
            if (directory + 16 > stream.Length) return list;
            stream.Position = directory + 12;
            var count = reader.ReadUInt16() + reader.ReadUInt16();
            if (directory + 16 + count * 8L > stream.Length) return list;
            for (var i = 0; i < count; i++) list.Add((reader.ReadUInt32(), reader.ReadUInt32()));
            return list;
        }

        // An entry's data, through the first child of each level below it.
        byte[]? Data(uint offset)
        {
            for (var depth = 0; depth < 3 && (offset & Subdirectory) != 0; depth++)
            {
                var children = Entries(offset);
                if (children.Count == 0) return null;
                offset = children[0].Offset;
            }
            if ((offset & Subdirectory) != 0 || root + offset + 8 > stream.Length) return null;
            stream.Position = root + offset;
            var rva = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            if (size == 0 || size > 16 * 1024 * 1024 || FileOffset(rva) is not { } at || at + size > stream.Length) return null;
            stream.Position = at;
            return reader.ReadBytes((int)size);
        }

        var types = Entries(0);
        var groups = types.FirstOrDefault(t => t.Id == RtGroupIcon);
        var icons = types.FirstOrDefault(t => t.Id == RtIcon);
        if ((groups.Offset & Subdirectory) == 0 || (icons.Offset & Subdirectory) == 0) return null;

        // The first group is the one Windows shows for the file: 6 bytes of header, then 14 bytes per image.
        var groupEntries = Entries(groups.Offset);
        if (groupEntries.Count == 0 || Data(groupEntries[0].Offset) is not { Length: >= 6 } group) return null;
        var images = new List<(int Size, int Bits, ushort Id, int At)>();
        var count = BitConverter.ToUInt16(group, 4);
        for (var i = 0; i < count && 6 + (i + 1) * 14 <= group.Length; i++)
        {
            var at = 6 + i * 14;
            images.Add((group[at] == 0 ? 256 : group[at], BitConverter.ToUInt16(group, at + 6), BitConverter.ToUInt16(group, at + 12), at));
        }
        if (images.Count == 0) return null;

        var big = images.Where(i => i.Size >= wanted).OrderBy(i => i.Size).ThenByDescending(i => i.Bits).ToList();
        var best = big.Count > 0 ? big[0] : images.OrderByDescending(i => i.Size).ThenByDescending(i => i.Bits).First();

        var iconEntry = Entries(icons.Offset).FirstOrDefault(e => (e.Id & Subdirectory) == 0 && e.Id == best.Id);
        if (iconEntry.Offset == 0 || Data(iconEntry.Offset) is not { Length: > 0 } image) return null;

        // A one-image .ico: its header, the group's description of the image with where the image starts, then the image.
        using var ico = new MemoryStream();
        using var writer = new BinaryWriter(ico);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(group, best.At, 8);
        writer.Write((uint)image.Length);
        writer.Write((uint)22);
        writer.Write(image);
        writer.Flush();
        return ico.ToArray();
    }
}
