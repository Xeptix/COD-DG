using System.Globalization;
using System.Text;

namespace CODDowngrader.Steam;

public sealed record DepotInfo(
    uint DepotId,
    string? Name,
    string? OsList,
    string? Language,
    bool LowViolence,
    uint? DlcAppId,
    uint? DepotFromApp,
    string? SharedInstall,
    ulong? PublicManifest,
    ulong? PublicSize)
{
    /// <summary>Steamworks redistributables (sharedinstall 1): installed once for every game, never part of a download.</summary>
    public bool IsRedistributable => SharedInstall == "1";

    /// <summary>A depot another app owns and this one uses, such as a Multiplayer app's share of the campaign's content.</summary>
    public bool IsBorrowed => DepotFromApp is not null && !IsRedistributable;

    public bool ForWindows => string.IsNullOrEmpty(OsList) || OsList.Contains("windows", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The part of an app's Steam product info this tool reads.</summary>
public sealed record AppInfo(
    uint AppId,
    string? Name,
    uint? PublicBuildId,
    IReadOnlyList<DepotInfo> Depots)
{
    public DepotInfo? Depot(uint depotId) => Depots.FirstOrDefault(d => d.DepotId == depotId);

    public static AppInfo From(uint appId, KvNode app)
    {
        var depotsNode = app["depots"];
        var depots = new List<DepotInfo>();

        foreach (var node in depotsNode?.Children ?? new List<KvNode>())
        {
            if (!uint.TryParse(node.Key, out var depotId)) continue;

            var config = node["config"];
            var manifest = node["manifests"]?["public"];
            // Newer product info nests { gid size download }; older puts the gid straight on "public".
            var gid = manifest?.Value ?? manifest?.Get("gid");

            depots.Add(new DepotInfo(
                depotId,
                node.Get("name"),
                config?.Get("oslist"),
                config?.Get("language"),
                config?.Get("lowviolence") == "1",
                ParseUInt(node.Get("dlcappid")),
                ParseUInt(node.Get("depotfromapp")),
                node.Get("sharedinstall"),
                ulong.TryParse(gid, out var g) ? g : null,
                ulong.TryParse(manifest?.Get("size"), out var s) ? s : null));
        }

        var pub = depotsNode?["branches"]?["public"];
        return new AppInfo(appId, app["common"]?.Get("name"), ParseUInt(pub?.Get("buildid")), depots);
    }

    static uint? ParseUInt(string? s) => uint.TryParse(s, out var v) ? v : null;
}

/// <summary>
/// Reads Steam's binary appcache\appinfo.vdf: the product info the client keeps for every app it has
/// seen. Versions 39-41 are understood; 41 moved every key name into a string table at the end.
/// </summary>
public static class AppInfoReader
{
    public static Dictionary<uint, AppInfo> Read(string path, IReadOnlySet<uint> appIds)
    {
        var result = new Dictionary<uint, AppInfo>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var magic = reader.ReadUInt32();
        var version = magic & 0xFF;
        if (magic >> 8 != 0x075644 || version is < 39 or > 41)
            throw new InvalidDataException($"Unsupported appinfo.vdf format 0x{magic:X8}.");
        reader.ReadUInt32(); // universe

        string[]? strings = null;
        var entriesEnd = stream.Length;
        if (version >= 41)
        {
            var tableOffset = reader.ReadInt64();
            if (tableOffset < stream.Position || tableOffset > stream.Length) throw new InvalidDataException("Bad appinfo.vdf string table offset.");
            var entriesStart = stream.Position;
            stream.Position = tableOffset;
            var count = reader.ReadUInt32();
            if (count > stream.Length - stream.Position) throw new InvalidDataException("Bad appinfo.vdf string table.");
            strings = new string[count];
            for (var i = 0; i < count; i++) strings[i] = ReadCString(reader);
            stream.Position = entriesStart;
            entriesEnd = tableOffset;
        }

        while (stream.Position + 8 <= entriesEnd)
        {
            var appId = reader.ReadUInt32();
            if (appId == 0) break;
            var size = reader.ReadUInt32();
            var end = stream.Position + size;
            if (end > entriesEnd) throw new InvalidDataException("Truncated appinfo.vdf entry.");

            if (appIds.Contains(appId))
            {
                reader.ReadUInt32();  // info state
                reader.ReadUInt32();  // last updated
                reader.ReadUInt64();  // PICS token
                reader.ReadBytes(20); // SHA-1 of the text form
                reader.ReadUInt32();  // change number
                if (version >= 40) reader.ReadBytes(20); // SHA-1 of the binary form

                var root = new KvNode("");
                ReadObject(reader, root, strings, end);
                var app = root.Children.FirstOrDefault(c => c.Key == "appinfo") ?? root;
                result[appId] = AppInfo.From(appId, app);
                if (result.Count == appIds.Count) break;
            }

            stream.Position = end;
        }

        return result;
    }

    static void ReadObject(BinaryReader reader, KvNode parent, string[]? strings, long end)
    {
        while (reader.BaseStream.Position < end)
        {
            var type = reader.ReadByte();
            if (type is 0x08 or 0x0B) return;

            string key;
            if (strings is null)
            {
                key = ReadCString(reader);
            }
            else
            {
                var index = reader.ReadInt32();
                if (index < 0 || index >= strings.Length) throw new InvalidDataException("Bad appinfo.vdf key index.");
                key = strings[index];
            }
            var inv = CultureInfo.InvariantCulture;

            switch (type)
            {
                case 0x00:
                    var child = new KvNode(key);
                    parent.Children.Add(child);
                    ReadObject(reader, child, strings, end);
                    break;
                case 0x01: parent.Children.Add(new KvNode(key, ReadCString(reader))); break;
                case 0x02 or 0x04 or 0x06: parent.Children.Add(new KvNode(key, reader.ReadInt32().ToString(inv))); break;
                case 0x03: parent.Children.Add(new KvNode(key, reader.ReadSingle().ToString(inv))); break;
                case 0x05: parent.Children.Add(new KvNode(key, ReadWideString(reader))); break;
                case 0x07: parent.Children.Add(new KvNode(key, reader.ReadUInt64().ToString(inv))); break;
                case 0x0A: parent.Children.Add(new KvNode(key, reader.ReadInt64().ToString(inv))); break;
                default: throw new InvalidDataException($"Unknown KeyValues type 0x{type:X2} in appinfo.vdf.");
            }
        }
    }

    static string ReadCString(BinaryReader reader)
    {
        var bytes = new List<byte>(32);
        byte b;
        while ((b = reader.ReadByte()) != 0) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    static string ReadWideString(BinaryReader reader)
    {
        var sb = new StringBuilder();
        char c;
        while ((c = (char)reader.ReadUInt16()) != '\0') sb.Append(c);
        return sb.ToString();
    }
}
