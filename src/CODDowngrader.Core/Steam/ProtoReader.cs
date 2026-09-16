namespace CODDowngrader.Steam;

/// <summary>
/// Just enough protobuf to read Steam's content manifests and DepotDownloader's depot.config: a
/// forward-only walk over varint, fixed-width and length-delimited fields. Unknown fields are skipped.
/// Every length is checked against the data before it is used, so a damaged file is an
/// InvalidDataException and nothing else.
/// </summary>
public ref struct ProtoReader
{
    readonly ReadOnlySpan<byte> _data;
    int _pos;

    public ProtoReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
        FieldNumber = 0;
        WireType = 0;
    }

    public int FieldNumber { get; private set; }

    public int WireType { get; private set; }

    public bool IsVarint => WireType == 0;

    public bool IsBytes => WireType == 2;

    public bool Next()
    {
        if (_pos >= _data.Length) return false;
        var key = ReadVarint();
        FieldNumber = (int)(key >> 3);
        WireType = (int)(key & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (_pos >= _data.Length) throw new InvalidDataException("Truncated varint.");
            var b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Varint is too long.");
        }
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = ReadVarint();
        if (length > (ulong)(_data.Length - _pos)) throw new InvalidDataException("Truncated field.");
        var slice = _data.Slice(_pos, (int)length);
        _pos += (int)length;
        return slice;
    }

    public void Skip()
    {
        switch (WireType)
        {
            case 0: ReadVarint(); break;
            case 1: Advance(8); break;
            case 2: ReadBytes(); break;
            case 5: Advance(4); break;
            default: throw new InvalidDataException($"Unsupported protobuf wire type {WireType}.");
        }
    }

    void Advance(int count)
    {
        if (count > _data.Length - _pos) throw new InvalidDataException("Truncated field.");
        _pos += count;
    }
}
