using System.Text;

namespace Layts.Core.Sii;

public sealed record BsiiField(uint Type, string Name);

public sealed class BsiiStruct
{
    public BsiiStruct(uint id, string name, List<BsiiField> fields, byte[] raw)
    {
        Id = id;
        Name = name;
        Fields = fields;
        Raw = raw;
    }

    public uint Id { get; }
    public string Name { get; }
    public List<BsiiField> Fields { get; }

    /// <summary>Unveränderte Bytes der Strukturdefinition (werden 1:1 zurückgeschrieben).</summary>
    public byte[] Raw { get; }

    public int IndexOf(string name)
    {
        for (int i = 0; i < Fields.Count; i++)
            if (Fields[i].Name == name) return i;
        return -1;
    }
}

/// <summary>
/// Binäres SII-Format („BSII“). Werte werden als rohe Bytes gehalten, sodass alles,
/// was nicht bearbeitet wird, beim Speichern Byte für Byte identisch bleibt.
/// </summary>
public sealed class BsiiDocument : ISiiDocument
{
    private readonly byte[] _header;
    private readonly List<object> _items = new();
    private readonly byte[] _tail = Array.Empty<byte>();

    public uint Version { get; }
    public string FormatName => $"Binär (BSII v{Version})";

    private BsiiDocument(byte[] data)
    {
        if (data.Length < 8) throw new InvalidDataException("Datei ist zu kurz.");
        _header = data[..8];
        Version = BitConverter.ToUInt32(data, 4);

        var r = new BsiiReader(data, 8);
        var structs = new Dictionary<uint, BsiiStruct>();

        while (r.Pos < data.Length)
        {
            int start = r.Pos;
            uint blockType = r.U32();

            if (blockType == 0)
            {
                if (r.U8() == 0)
                {
                    _tail = data[start..];
                    break;
                }

                uint id = r.U32();
                string name = r.Str();
                var fields = new List<BsiiField>();
                while (true)
                {
                    uint type = r.U32();
                    if (type == 0) break;
                    string fieldName = r.Str();
                    if (type == 0x37)
                    {
                        uint count = r.U32();
                        for (uint i = 0; i < count; i++) { r.U32(); r.Str(); }
                    }
                    fields.Add(new BsiiField(type, fieldName));
                }

                var def = new BsiiStruct(id, name, fields, data[start..r.Pos]);
                structs[id] = def;
                _items.Add(def);
            }
            else
            {
                if (!structs.TryGetValue(blockType, out var def))
                    throw new InvalidDataException($"Unbekannte Struktur {blockType} an Position {start}.");

                int idStart = r.Pos;
                string idText = r.Id();
                byte[] idRaw = data[idStart..r.Pos];

                var values = new byte[def.Fields.Count][];
                for (int i = 0; i < values.Length; i++)
                {
                    int valueStart = r.Pos;
                    r.SkipValue(def.Fields[i].Type);
                    values[i] = data[valueStart..r.Pos];
                }

                _items.Add(new BsiiUnit(def, idText, idRaw, values));
            }
        }
    }

    public static BsiiDocument Parse(byte[] data) => new(data);

    public IEnumerable<ISiiUnit> Units(string typeName) =>
        _items.OfType<BsiiUnit>().Where(u => u.TypeName == typeName);

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        ms.Write(_header);
        foreach (var item in _items)
        {
            switch (item)
            {
                case BsiiStruct s:
                    ms.Write(s.Raw);
                    break;
                case BsiiUnit u:
                    ms.Write(BitConverter.GetBytes(u.Struct.Id));
                    ms.Write(u.IdRaw);
                    foreach (var v in u.Values) ms.Write(v);
                    break;
            }
        }
        ms.Write(_tail);
        return ms.ToArray();
    }
}

internal sealed class BsiiReader
{
    private readonly byte[] _d;
    public int Pos;

    public BsiiReader(byte[] data, int pos = 0)
    {
        _d = data;
        Pos = pos;
    }

    private void Need(long n)
    {
        if (n < 0 || Pos + n > _d.Length)
            throw new InvalidDataException($"Unerwartetes Dateiende an Position {Pos}.");
    }

    public byte U8() { Need(1); return _d[Pos++]; }
    public uint U32() { Need(4); var v = BitConverter.ToUInt32(_d, Pos); Pos += 4; return v; }
    public ulong U64() { Need(8); var v = BitConverter.ToUInt64(_d, Pos); Pos += 8; return v; }

    public string Str()
    {
        long n = U32();
        Need(n);
        var s = Encoding.UTF8.GetString(_d, Pos, (int)n);
        Pos += (int)n;
        return s;
    }

    public void Skip(long n)
    {
        Need(n);
        Pos += (int)n;
    }

    public string Id()
    {
        byte len = U8();
        if (len == 0xFF) return "_nameless." + FormatNameless(U64());
        if (len == 0) return "null";
        var parts = new string[len];
        for (int i = 0; i < len; i++) parts[i] = SiiToken.Decode(U64());
        return string.Join('.', parts);
    }

    private static string FormatNameless(ulong v)
    {
        var hex = v.ToString("x");
        var sb = new StringBuilder();
        for (int i = 0; i < hex.Length; i++)
        {
            if (i > 0 && (hex.Length - i) % 4 == 0) sb.Append('.');
            sb.Append(hex[i]);
        }
        return sb.ToString();
    }

    public void SkipValue(uint type)
    {
        switch (type)
        {
            case 0x01:
                Str();
                return;
            case 0x02:
            {
                uint n = U32();
                for (uint i = 0; i < n; i++) Str();
                return;
            }
            case 0x39 or 0x3B or 0x3D:
                Id();
                return;
            case 0x3A or 0x3C or 0x3E:
            {
                uint n = U32();
                for (uint i = 0; i < n; i++) Id();
                return;
            }
        }

        int size = ScalarSize(type);
        if (size > 0)
        {
            Skip(size);
            return;
        }

        if (IsArrayType(type))
        {
            uint n = U32();
            Skip((long)n * ScalarSize(type - 1));
            return;
        }

        throw new InvalidDataException($"Unbekannter Datentyp 0x{type:X2} an Position {Pos}.");
    }

    public static int ScalarSize(uint type) => type switch
    {
        0x03 => 8,  // Token
        0x05 => 4,  // float
        0x07 => 8,  // vec2
        0x09 => 12, // vec3
        0x11 => 12, // vec3i
        0x17 => 16, // vec4
        0x19 => 32, // vec8 (Position + Rotation)
        0x25 => 4,  // int32
        0x27 => 4,  // uint32
        0x29 => 2,  // int16
        0x2B => 2,  // uint16
        0x2F => 4,  // uint32
        0x31 => 8,  // int64
        0x33 => 8,  // uint64
        0x35 => 1,  // bool
        0x37 => 4,  // Ordinal-String
        _ => 0,
    };

    public static bool IsArrayType(uint type) =>
        type is 0x04 or 0x06 or 0x08 or 0x0A or 0x12 or 0x18 or 0x1A or 0x26 or 0x28
            or 0x2A or 0x2C or 0x32 or 0x34 or 0x36;
}

public sealed class BsiiUnit : ISiiUnit
{
    internal BsiiUnit(BsiiStruct def, string id, byte[] idRaw, byte[][] values)
    {
        Struct = def;
        Id = id;
        IdRaw = idRaw;
        Values = values;
    }

    public BsiiStruct Struct { get; }
    public string TypeName => Struct.Name;
    public string Id { get; }
    internal byte[] IdRaw { get; }
    internal byte[][] Values { get; }

    public IEnumerable<string> FieldNames => Struct.Fields.Select(f => f.Name);
    public bool HasField(string name) => Struct.IndexOf(name) >= 0;

    private int Idx(string name)
    {
        int i = Struct.IndexOf(name);
        if (i < 0) throw new KeyNotFoundException($"Feld „{name}“ fehlt in „{TypeName}“.");
        return i;
    }

    private uint TypeAt(int i) => Struct.Fields[i].Type;

    private InvalidOperationException WrongType(string name, string expected) =>
        new($"Feld „{name}“ in „{TypeName}“ ist kein(e) {expected} (Typ 0x{TypeAt(Idx(name)):X2}).");

    public long GetInteger(string name)
    {
        int i = Idx(name);
        var v = Values[i];
        return TypeAt(i) switch
        {
            0x25 => BitConverter.ToInt32(v, 0),
            0x27 or 0x2F or 0x37 => BitConverter.ToUInt32(v, 0),
            0x29 => BitConverter.ToInt16(v, 0),
            0x2B => BitConverter.ToUInt16(v, 0),
            0x31 => BitConverter.ToInt64(v, 0),
            0x33 => (long)Math.Min(BitConverter.ToUInt64(v, 0), (ulong)long.MaxValue),
            0x35 => v[0],
            _ => throw WrongType(name, "Zahl"),
        };
    }

    public void SetInteger(string name, long value)
    {
        int i = Idx(name);
        Values[i] = TypeAt(i) switch
        {
            0x25 => BitConverter.GetBytes((int)Math.Clamp(value, int.MinValue, int.MaxValue)),
            0x27 or 0x2F => BitConverter.GetBytes((uint)Math.Clamp(value, 0L, (long)uint.MaxValue)),
            0x29 => BitConverter.GetBytes((short)Math.Clamp(value, short.MinValue, short.MaxValue)),
            0x2B => BitConverter.GetBytes((ushort)Math.Clamp(value, 0L, ushort.MaxValue)),
            0x31 => BitConverter.GetBytes(value),
            0x33 => BitConverter.GetBytes((ulong)Math.Max(0L, value)),
            0x35 => new[] { (byte)(value != 0 ? 1 : 0) },
            _ => throw WrongType(name, "Zahl"),
        };
    }

    public bool IsFloat(string name) => HasField(name) && TypeAt(Idx(name)) == 0x05;
    public bool IsFloatArray(string name) => HasField(name) && TypeAt(Idx(name)) == 0x06;

    public float GetFloat(string name)
    {
        int i = Idx(name);
        if (TypeAt(i) != 0x05) throw WrongType(name, "Kommazahl");
        return BitConverter.ToSingle(Values[i], 0);
    }

    public void SetFloat(string name, float value)
    {
        int i = Idx(name);
        if (TypeAt(i) != 0x05) throw WrongType(name, "Kommazahl");
        Values[i] = BitConverter.GetBytes(value);
    }

    public void SetFloatArrayAll(string name, float value)
    {
        int i = Idx(name);
        if (TypeAt(i) != 0x06) throw WrongType(name, "Kommazahl-Liste");
        int count = (int)BitConverter.ToUInt32(Values[i], 0);
        var bytes = new byte[4 + count * 4];
        BitConverter.GetBytes((uint)count).CopyTo(bytes, 0);
        var item = BitConverter.GetBytes(value);
        for (int k = 0; k < count; k++) item.CopyTo(bytes, 4 + k * 4);
        Values[i] = bytes;
    }

    public string GetString(string name)
    {
        int i = Idx(name);
        var v = Values[i];
        return TypeAt(i) switch
        {
            0x01 => Encoding.UTF8.GetString(v, 4, v.Length - 4),
            0x03 => SiiToken.Decode(BitConverter.ToUInt64(v, 0)),
            _ => throw WrongType(name, "Text"),
        };
    }

    public List<string> GetStringArray(string name)
    {
        int i = Idx(name);
        if (TypeAt(i) != 0x02) throw WrongType(name, "Text-Liste");
        var r = new BsiiReader(Values[i]);
        uint count = r.U32();
        var list = new List<string>((int)count);
        for (uint k = 0; k < count; k++) list.Add(r.Str());
        return list;
    }

    public int GetArrayCount(string name)
    {
        int i = Idx(name);
        uint t = TypeAt(i);
        bool isArray = BsiiReader.IsArrayType(t) || t is 0x02 or 0x3A or 0x3C or 0x3E;
        if (!isArray) throw WrongType(name, "Liste");
        return (int)BitConverter.ToUInt32(Values[i], 0);
    }

    public List<string> GetTokenArray(string name)
    {
        int i = Idx(name);
        if (TypeAt(i) != 0x04) throw WrongType(name, "Token-Liste");
        var v = Values[i];
        int count = (int)BitConverter.ToUInt32(v, 0);
        var list = new List<string>(count);
        for (int k = 0; k < count; k++) list.Add(SiiToken.Decode(BitConverter.ToUInt64(v, 4 + k * 8)));
        return list;
    }

    public void SetTokenArray(string name, IReadOnlyList<string> values)
    {
        int i = Idx(name);
        if (TypeAt(i) != 0x04) throw WrongType(name, "Token-Liste");
        var bytes = new byte[4 + values.Count * 8];
        BitConverter.GetBytes((uint)values.Count).CopyTo(bytes, 0);
        for (int k = 0; k < values.Count; k++)
            BitConverter.GetBytes(SiiToken.Encode(values[k])).CopyTo(bytes, 4 + k * 8);
        Values[i] = bytes;
    }

    public List<long> GetIntegerArray(string name)
    {
        int i = Idx(name);
        var v = Values[i];
        int count = (int)BitConverter.ToUInt32(v, 0);
        var list = new List<long>(count);
        switch (TypeAt(i))
        {
            case 0x26:
                for (int k = 0; k < count; k++) list.Add(BitConverter.ToInt32(v, 4 + k * 4));
                break;
            case 0x28:
                for (int k = 0; k < count; k++) list.Add(BitConverter.ToUInt32(v, 4 + k * 4));
                break;
            default:
                throw WrongType(name, "Zahlen-Liste");
        }
        return list;
    }

    public void SetIntegerArray(string name, IReadOnlyList<long> values)
    {
        int i = Idx(name);
        uint t = TypeAt(i);
        if (t is not (0x26 or 0x28)) throw WrongType(name, "Zahlen-Liste");
        var bytes = new byte[4 + values.Count * 4];
        BitConverter.GetBytes((uint)values.Count).CopyTo(bytes, 0);
        for (int k = 0; k < values.Count; k++)
        {
            byte[] item = t == 0x26
                ? BitConverter.GetBytes((int)Math.Clamp(values[k], int.MinValue, int.MaxValue))
                : BitConverter.GetBytes((uint)Math.Clamp(values[k], 0L, (long)uint.MaxValue));
            item.CopyTo(bytes, 4 + k * 4);
        }
        Values[i] = bytes;
    }

    public List<string> GetIdArray(string name)
    {
        int i = Idx(name);
        if (TypeAt(i) is not (0x3A or 0x3C or 0x3E)) throw WrongType(name, "ID-Liste");
        var r = new BsiiReader(Values[i]);
        uint count = r.U32();
        var list = new List<string>((int)count);
        for (uint k = 0; k < count; k++) list.Add(r.Id());
        return list;
    }

    public void GrowIdArray(string name, int count)
    {
        int i = Idx(name);
        if (TypeAt(i) is not (0x3A or 0x3C or 0x3E)) throw WrongType(name, "ID-Liste");
        var v = Values[i];
        int existing = (int)BitConverter.ToUInt32(v, 0);
        if (existing >= count) return;

        // Bestehende IDs bleiben unverändert, neue Einträge sind „null“ (ein 0-Byte).
        var bytes = new byte[v.Length + (count - existing)];
        BitConverter.GetBytes((uint)count).CopyTo(bytes, 0);
        Array.Copy(v, 4, bytes, 4, v.Length - 4);
        Values[i] = bytes;
    }
}
