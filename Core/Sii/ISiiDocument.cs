namespace Layts.Core.Sii;

/// <summary>Ein geladenes SII-Dokument (binär oder Text).</summary>
public interface ISiiDocument
{
    string FormatName { get; }
    IEnumerable<ISiiUnit> Units(string typeName);
    byte[] Serialize();
}

/// <summary>Eine Einheit (Block) im Spielstand, z. B. „bank“ oder „economy“.</summary>
public interface ISiiUnit
{
    string TypeName { get; }
    string Id { get; }
    IEnumerable<string> FieldNames { get; }
    bool HasField(string name);

    long GetInteger(string name);
    void SetInteger(string name, long value);

    float GetFloat(string name);
    void SetFloat(string name, float value);
    bool IsFloat(string name);
    bool IsFloatArray(string name);
    void SetFloatArrayAll(string name, float value);

    string GetString(string name);
    List<string> GetStringArray(string name);

    int GetArrayCount(string name);
    List<string> GetTokenArray(string name);
    void SetTokenArray(string name, IReadOnlyList<string> values);
    List<long> GetIntegerArray(string name);
    void SetIntegerArray(string name, IReadOnlyList<long> values);
    List<string> GetIdArray(string name);

    /// <summary>Vergrößert ein ID-Array auf <paramref name="count"/> Einträge (neue Einträge = null).</summary>
    void GrowIdArray(string name, int count);
}

public static class SiiDocumentExtensions
{
    public static ISiiUnit? First(this ISiiDocument doc, string typeName) => doc.Units(typeName).FirstOrDefault();
}
