using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Layts.Core.Sii;

/// <summary>
/// Text-Format („SiiNunit“). Wird verwendet, wenn im Spiel g_save_format "2" eingestellt ist.
/// Unveränderte Zeilen werden 1:1 übernommen.
/// </summary>
public sealed class TextSiiDocument : ISiiDocument
{
    private static readonly Regex HeaderRx = new(@"^\s*([A-Za-z0-9_]+)\s*:\s*([A-Za-z0-9_.]+)\s*(\{)?\s*$", RegexOptions.Compiled);

    private readonly List<object> _parts = new();
    private readonly string _newLine;

    public string FormatName => "Text (SiiNunit)";

    private TextSiiDocument(string text)
    {
        _newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');

        int i = 0;
        while (i < lines.Length)
        {
            var m = HeaderRx.Match(lines[i]);
            if (!m.Success)
            {
                _parts.Add(lines[i]);
                i++;
                continue;
            }

            int j = i + 1;
            string? openLine = null;
            if (!m.Groups[3].Success)
            {
                if (j < lines.Length && lines[j].Trim() == "{")
                {
                    openLine = lines[j];
                    j++;
                }
                else
                {
                    _parts.Add(lines[i]);
                    i++;
                    continue;
                }
            }

            var body = new List<string>();
            while (j < lines.Length && lines[j].Trim() != "}")
            {
                body.Add(lines[j]);
                j++;
            }

            string close = j < lines.Length ? lines[j] : "}";
            _parts.Add(new TextUnit(m.Groups[1].Value, m.Groups[2].Value, lines[i], openLine, body, close));
            i = j + 1;
        }
    }

    public static TextSiiDocument Parse(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        return new TextSiiDocument(text);
    }

    public IEnumerable<ISiiUnit> Units(string typeName) =>
        _parts.OfType<TextUnit>().Where(u => u.TypeName == typeName);

    public byte[] Serialize()
    {
        var lines = new List<string>();
        foreach (var part in _parts)
        {
            if (part is TextUnit u) lines.AddRange(u.AllLines());
            else lines.Add((string)part);
        }
        return new UTF8Encoding(false).GetBytes(string.Join(_newLine, lines));
    }
}

public sealed class TextUnit : ISiiUnit
{
    private static readonly Regex FieldRx = new(@"^(\s*)([A-Za-z0-9_]+)(?:\[(\d*)\])?\s*:\s?(.*)$", RegexOptions.Compiled);

    private readonly string _header;
    private readonly string? _openLine;
    private readonly List<string> _body;
    private readonly string _close;

    internal TextUnit(string type, string id, string header, string? openLine, List<string> body, string close)
    {
        TypeName = type;
        Id = id;
        _header = header;
        _openLine = openLine;
        _body = body;
        _close = close;
    }

    public string TypeName { get; }
    public string Id { get; }

    internal IEnumerable<string> AllLines()
    {
        yield return _header;
        if (_openLine != null) yield return _openLine;
        foreach (var l in _body) yield return l;
        yield return _close;
    }

    private Match? MatchLine(int index)
    {
        var m = FieldRx.Match(_body[index]);
        return m.Success ? m : null;
    }

    private int FindScalar(string name)
    {
        for (int i = 0; i < _body.Count; i++)
        {
            var m = MatchLine(i);
            if (m != null && m.Groups[2].Value == name && !m.Groups[3].Success) return i;
        }
        return -1;
    }

    private List<int> FindElements(string name)
    {
        var list = new List<int>();
        for (int i = 0; i < _body.Count; i++)
        {
            var m = MatchLine(i);
            if (m != null && m.Groups[2].Value == name && m.Groups[3].Success) list.Add(i);
        }
        return list;
    }

    private string Indent()
    {
        foreach (var l in _body)
        {
            var m = FieldRx.Match(l);
            if (m.Success) return m.Groups[1].Value;
        }
        return " ";
    }

    private string RawValue(string name)
    {
        int i = FindScalar(name);
        if (i < 0) throw new KeyNotFoundException($"Feld „{name}“ fehlt in „{TypeName}“.");
        return MatchLine(i)!.Groups[4].Value.Trim();
    }

    private void SetRawValue(string name, string value)
    {
        int i = FindScalar(name);
        if (i < 0) throw new KeyNotFoundException($"Feld „{name}“ fehlt in „{TypeName}“.");
        var m = MatchLine(i)!;
        _body[i] = $"{m.Groups[1].Value}{name}: {value}";
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            return v[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return v;
    }

    private static float ParseFloat(string v)
    {
        v = v.Trim();
        if (v.StartsWith('&'))
            return BitConverter.Int32BitsToSingle(int.Parse(v[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string FormatFloat(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public IEnumerable<string> FieldNames =>
        Enumerable.Range(0, _body.Count).Select(MatchLine).Where(m => m != null)
            .Select(m => m!.Groups[2].Value).Distinct();

    public bool HasField(string name) => FindScalar(name) >= 0 || FindElements(name).Count > 0;

    public long GetInteger(string name)
    {
        var v = Unquote(RawValue(name));
        if (v == "nil") return -1;
        if (v == "true") return 1;
        if (v == "false") return 0;
        return long.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    public void SetInteger(string name, long value) =>
        SetRawValue(name, value.ToString(CultureInfo.InvariantCulture));

    public bool IsFloat(string name) => FindElements(name).Count == 0 && FindScalar(name) >= 0;
    public bool IsFloatArray(string name) => FindElements(name).Count > 0;

    public float GetFloat(string name) => ParseFloat(RawValue(name));
    public void SetFloat(string name, float value) => SetRawValue(name, FormatFloat(value));

    public void SetFloatArrayAll(string name, float value)
    {
        var count = GetArrayValues(name).Count;
        SetArray(name, Enumerable.Repeat(FormatFloat(value), count).ToList());
    }

    public string GetString(string name) => Unquote(RawValue(name));

    public List<string> GetStringArray(string name) => GetArrayValues(name).Select(Unquote).ToList();

    private List<string> GetArrayValues(string name) =>
        FindElements(name).Select(i => MatchLine(i)!.Groups[4].Value.Trim()).ToList();

    private void SetArray(string name, IReadOnlyList<string> values)
    {
        var indices = FindElements(name);
        int scalar = FindScalar(name);
        if (scalar >= 0) indices.Add(scalar);
        indices.Sort();

        string indent = Indent();
        int insertAt = indices.Count > 0 ? indices[0] : _body.Count;
        for (int k = indices.Count - 1; k >= 0; k--) _body.RemoveAt(indices[k]);

        var lines = new List<string> { $"{indent}{name}: {values.Count}" };
        for (int k = 0; k < values.Count; k++) lines.Add($"{indent}{name}[{k}]: {values[k]}");
        _body.InsertRange(insertAt, lines);
    }

    public int GetArrayCount(string name)
    {
        var elements = FindElements(name);
        if (elements.Count > 0) return elements.Count;
        return FindScalar(name) >= 0 ? (int)GetInteger(name) : 0;
    }

    public List<string> GetTokenArray(string name) => GetArrayValues(name).Select(Unquote).ToList();

    public void SetTokenArray(string name, IReadOnlyList<string> values) => SetArray(name, values);

    public List<long> GetIntegerArray(string name) =>
        GetArrayValues(name).Select(v => long.Parse(Unquote(v), CultureInfo.InvariantCulture)).ToList();

    public void SetIntegerArray(string name, IReadOnlyList<long> values) =>
        SetArray(name, values.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToList());

    public List<string> GetIdArray(string name) => GetArrayValues(name).Select(Unquote).ToList();

    public void GrowIdArray(string name, int count)
    {
        var values = GetArrayValues(name);
        if (values.Count >= count) return;
        while (values.Count < count) values.Add("null");
        SetArray(name, values);
    }
}
