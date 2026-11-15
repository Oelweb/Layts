using System.Text;

namespace Layts.Games.Bus;

/// <summary>One property tag found in an Unreal save (GVAS, UE 5.4+ tag layout).</summary>
public sealed record GvasTag(int Offset, string Name, string Type, int Size, byte Flags, int ValueOffset);

/// <summary>
/// Minimal, read-mostly view of an Unreal Engine save file.
/// It does not rebuild the file: it finds property tags and changes fixed-size values
/// (float, double, int) in place, so everything else stays byte-for-byte identical.
/// </summary>
public sealed class BusGvas
{
    private BusGvas(byte[] data)
    {
        Data = data;
        Tags = Scan(data);
    }

    public byte[] Data { get; }
    public IReadOnlyList<GvasTag> Tags { get; }

    public static BusGvas Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 4 || Encoding.ASCII.GetString(data, 0, 4) != "GVAS")
            throw new InvalidDataException("Not an Unreal save file.");
        return new BusGvas(data);
    }

    /// <summary>
    /// Tag layout: int32 len, name\0, int32 len, type\0, int32 typeParams, int32 size, byte flags, value.
    /// Only tags without type parameters are collected – that covers all simple values.
    /// </summary>
    private static List<GvasTag> Scan(byte[] d)
    {
        var tags = new List<GvasTag>();
        for (int i = 0; i + 16 < d.Length; i++)
        {
            int nameLen = BitConverter.ToInt32(d, i);
            if (nameLen < 2 || nameLen > 64) continue;
            int nameEnd = i + 4 + nameLen;
            if (nameEnd + 4 > d.Length || d[nameEnd - 1] != 0 || !IsIdentifier(d, i + 4, nameLen - 1)) continue;

            int typeLen = BitConverter.ToInt32(d, nameEnd);
            if (typeLen < 5 || typeLen > 40) continue;
            int typeEnd = nameEnd + 4 + typeLen;
            if (typeEnd + 9 > d.Length || d[typeEnd - 1] != 0 || !IsIdentifier(d, nameEnd + 4, typeLen - 1)) continue;

            var type = Encoding.ASCII.GetString(d, nameEnd + 4, typeLen - 1);
            if (!type.EndsWith("Property", StringComparison.Ordinal)) continue;

            int typeParams = BitConverter.ToInt32(d, typeEnd);
            if (typeParams < 0 || typeParams > 8) continue;
            if (typeParams != 0)
            {
                // Containers (ArrayProperty, MapProperty, StructProperty …) carry nested type names.
                // Their position is enough to find what is inside them; the value is never edited.
                tags.Add(new GvasTag(i, Encoding.ASCII.GetString(d, i + 4, nameLen - 1), type, -1, 0xFF, -1));
                i = typeEnd + 3;
                continue;
            }
            int size = BitConverter.ToInt32(d, typeEnd + 4);
            byte flags = d[typeEnd + 8];
            var name = Encoding.ASCII.GetString(d, i + 4, nameLen - 1);
            tags.Add(new GvasTag(i, name, type, size, flags, typeEnd + 9));
            i = typeEnd + 8;
        }
        return tags;
    }

    private static bool IsIdentifier(byte[] d, int start, int length)
    {
        if (length <= 0) return false;
        byte first = d[start];
        if (!(first is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')) return false;
        for (int k = 1; k < length; k++)
        {
            byte c = d[start + k];
            if (!(c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_'))
                return false;
        }
        return true;
    }

    public IEnumerable<GvasTag> Find(string name, string type) =>
        Tags.Where(t => t.Name == name && t.Type == type);

    /// <summary>Simple values must not carry an array index or GUID (flags 0) to be edited safely.</summary>
    private static bool IsPlain(GvasTag t) => (t.Flags & 0x0F) == 0;

    public float ReadFloat(GvasTag t) => BitConverter.ToSingle(Data, t.ValueOffset);
    public double ReadDouble(GvasTag t) => BitConverter.ToDouble(Data, t.ValueOffset);
    public int ReadInt(GvasTag t) => BitConverter.ToInt32(Data, t.ValueOffset);

    public string ReadName(GvasTag t)
    {
        int len = BitConverter.ToInt32(Data, t.ValueOffset);
        if (len > 0 && len < 256 && t.ValueOffset + 4 + len <= Data.Length)
            return Encoding.Latin1.GetString(Data, t.ValueOffset + 4, len - 1);
        if (len < 0 && len > -256) // UTF-16
            return Encoding.Unicode.GetString(Data, t.ValueOffset + 4, (-len - 1) * 2);
        return "";
    }

    public bool WriteFloat(GvasTag t, float value)
    {
        if (t.Type != "FloatProperty" || t.Size != 4 || !IsPlain(t)) return false;
        BitConverter.GetBytes(value).CopyTo(Data, t.ValueOffset);
        return true;
    }

    public bool WriteDouble(GvasTag t, double value)
    {
        if (t.Type != "DoubleProperty" || t.Size != 8 || !IsPlain(t)) return false;
        BitConverter.GetBytes(value).CopyTo(Data, t.ValueOffset);
        return true;
    }

    /// <summary>Tags between the start of the named array property and the next array property.</summary>
    public List<GvasTag> ArrayRegion(string arrayName)
    {
        var start = Tags.FirstOrDefault(t => t.Name == arrayName && t.Type == "ArrayProperty");
        if (start == null) return new List<GvasTag>();
        var end = Tags.FirstOrDefault(t => t.Offset > start.Offset && t.Type == "ArrayProperty");
        int endOffset = end?.Offset ?? Data.Length;
        return Tags.Where(t => t.Offset > start.Offset && t.Offset < endOffset).ToList();
    }

    public void Save(string path)
    {
        var temp = path + ".layts-tmp";
        File.WriteAllBytes(temp, Data);
        File.Move(temp, path, overwrite: true);
    }
}
