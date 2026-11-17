using System.Buffers.Binary;
using System.Text;

namespace Layts.Games.Spm;

/// <summary>One field of a serialized Insomniac object.</summary>
public sealed class SpmField
{
    public required string Name { get; init; }
    public byte Type { get; init; }
    public int Count { get; init; }

    /// <summary>Absolute offset of the (first) value in the file.</summary>
    public int Offset { get; init; }

    /// <summary>Child object(s) for type 0x0D.</summary>
    public List<SpmObject> Objects { get; } = new();

    public SpmObject? Object => Objects.Count > 0 ? Objects[0] : null;
}

public sealed class SpmObject
{
    public Dictionary<string, SpmField> Fields { get; } = new(StringComparer.Ordinal);
    public SpmField? this[string name] => Fields.TryGetValue(name, out var f) ? f : null;
}

/// <summary>
/// Insomniac "DAT1" container ("1TAD") as used by the Marvel's Spider-Man Remastered PC saves.
/// Sections hold serialized objects (magic 0x03150044). layts only changes values in place –
/// same size, same layout – so everything it doesn't understand stays byte-identical.
/// </summary>
public sealed class SpmDat1
{
    private const uint Dat1Magic = 0x44415431; // "1TAD" read as little endian
    private const uint ObjectMagic = 0x03150044;

    public const byte TU8 = 0x00, TU16 = 0x01, TU32 = 0x02, TU64 = 0x03, TI8 = 0x04, TI16 = 0x05, TI32 = 0x06,
        TFloat = 0x08, TString = 0x0A, TObject = 0x0D, TBool = 0x0F, TId = 0x11, TNull = 0x13;

    private SpmDat1(string path, byte[] data)
    {
        FilePath = path;
        Data = data;
    }

    public string FilePath { get; }
    public byte[] Data { get; }

    /// <summary>All top-level objects that could be read, in file order.</summary>
    public List<SpmObject> Objects { get; } = new();

    public static SpmDat1 Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Dat1Magic)
            throw new InvalidDataException("Not a Spider-Man save file.");

        var file = new SpmDat1(path, data);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        if (count < 0 || 16 + count * 12 > data.Length) throw new InvalidDataException("Damaged save file.");

        for (int i = 0; i < count; i++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16 + i * 12 + 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16 + i * 12 + 8));
            if (offset < 0 || size <= 0 || offset + size > data.Length) continue;
            file.ReadSection(offset, offset + size);
        }
        return file;
    }

    private void ReadSection(int start, int end)
    {
        // Some sections start with a 4/8 byte tag, objects may be separated by zero padding.
        int p = start;
        int skipped = 0;
        while (p + 16 <= end)
        {
            if (IsObjectAt(p))
            {
                try
                {
                    var obj = ReadObject(ref p, end);
                    Objects.Add(obj);
                    skipped = 0;
                    continue;
                }
                catch
                {
                    return; // unknown content – leave this section alone
                }
            }
            p += 4;
            skipped += 4;
            if (skipped > 12) return;
        }
    }

    private bool IsObjectAt(int p) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(p)) == 0 &&
        BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(p + 4)) == ObjectMagic;

    private SpmObject ReadObject(ref int p, int end)
    {
        if (p + 16 > end || !IsObjectAt(p)) throw new InvalidDataException("Object expected.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p + 8));
        int length = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p + 12));
        int start = p + 16;
        if (count < 0 || length < count * 12 || start + length > end) throw new InvalidDataException("Bad object header.");

        var obj = new SpmObject();
        int q = start + count * 12;
        for (int i = 0; i < count; i++)
        {
            int entry = start + i * 8;
            int items = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(entry + 4)) >> 4;
            byte type = Data[entry + 7];
            int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(start + count * 8 + i * 4));
            string name = ReadCString(nameOffset);

            var field = new SpmField { Name = name, Type = type, Count = items, Offset = q };
            for (int k = 0; k < items; k++)
            {
                if (type == TObject)
                {
                    field.Objects.Add(ReadObject(ref q, start + length));
                }
                else
                {
                    q += ValueSize(type, q);
                }
            }
            obj.Fields[name] = field;
        }
        p = start + length;
        return obj;
    }

    private int ValueSize(byte type, int at) => type switch
    {
        TU8 or TI8 or TBool or TNull => 1,
        TU16 or TI16 => 2,
        TU32 or TI32 or TFloat => 4,
        TU64 or TId => 8,
        TString => Align4(at + 16 + BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(at)) + 1) - at,
        _ => throw new InvalidDataException($"Unknown value type 0x{type:X2}."),
    };

    private static int Align4(int v) => (v + 3) & ~3;

    private string ReadCString(int offset)
    {
        if (offset < 0 || offset >= Data.Length) return "";
        int e = Array.IndexOf(Data, (byte)0, offset);
        if (e < 0) e = Data.Length;
        return Encoding.ASCII.GetString(Data, offset, e - offset);
    }

    // ---------- Lookup ----------

    /// <summary>First top-level object that has the given field.</summary>
    public SpmObject? FindObjectWith(string field) => Objects.FirstOrDefault(o => o.Fields.ContainsKey(field));

    // ---------- Values ----------

    public bool GetBool(SpmField f, int index = 0) => Data[f.Offset + index] != 0;

    public void SetBool(SpmField f, bool value, int index = 0)
    {
        if (f.Type != TBool) throw new InvalidOperationException(f.Name + " is not a bool.");
        Data[f.Offset + index] = value ? (byte)1 : (byte)0;
    }

    public float GetFloat(SpmField f) => f.Type == TFloat ? BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan(f.Offset)) : GetLong(f);

    public void SetFloat(SpmField f, float value)
    {
        if (f.Type != TFloat) throw new InvalidOperationException(f.Name + " is not a float.");
        BinaryPrimitives.WriteSingleLittleEndian(Data.AsSpan(f.Offset), value);
    }

    public long GetLong(SpmField f) => f.Type switch
    {
        TU8 => Data[f.Offset],
        TI8 => (sbyte)Data[f.Offset],
        TBool => Data[f.Offset],
        TU16 => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(f.Offset)),
        TI16 => BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan(f.Offset)),
        TU32 => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(f.Offset)),
        TI32 => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(f.Offset)),
        TU64 or TId => (long)BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(f.Offset)),
        TFloat => (long)BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan(f.Offset)),
        _ => 0,
    };

    public string GetString(SpmField f)
    {
        if (f.Type != TString) return "";
        int len = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(f.Offset));
        return Encoding.ASCII.GetString(Data, f.Offset + 16, len);
    }

    /// <summary>Replaces a string value with one of the same length (keeps the layout) and updates both hashes.</summary>
    public bool SetStringSameLength(SpmField f, string value)
    {
        if (f.Type != TString) return false;
        int len = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(f.Offset));
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length != len) return false;
        BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(f.Offset + 4), SpmHash.Crc32(value));
        BinaryPrimitives.WriteUInt64LittleEndian(Data.AsSpan(f.Offset + 8), SpmHash.Crc64(value));
        bytes.CopyTo(Data, f.Offset + 16);
        return true;
    }

    public void Save()
    {
        var temp = FilePath + ".layts-tmp";
        File.WriteAllBytes(temp, Data);
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>Insomniac string hashes (verified against values stored in real saves).</summary>
public static class SpmHash
{
    private static readonly uint[] T32 = BuildTable32();
    private static readonly ulong[] T64 = BuildTable64();

    private static uint[] BuildTable32()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static ulong[] BuildTable64()
    {
        var t = new ulong[256];
        for (ulong i = 0; i < 256; i++)
        {
            ulong c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xC96C5795D7870F42ul : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(string s)
    {
        uint c = 0xEDB88320u;
        foreach (byte b in Encoding.ASCII.GetBytes(s)) c = (c >> 8) ^ T32[(c ^ b) & 0xFF];
        return c;
    }

    public static ulong Crc64(string s)
    {
        ulong c = 0xC96C5795D7870F42ul;
        foreach (byte b in Encoding.ASCII.GetBytes(s.ToLowerInvariant().Replace('\\', '/'))) c = (c >> 8) ^ T64[(c ^ b) & 0xFF];
        return (c >> 2) | 0x8000000000000000ul;
    }
}
