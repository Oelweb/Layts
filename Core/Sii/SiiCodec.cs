using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Layts.Core.Sii;

public enum SiiFormat { Unknown, Binary, Text }

/// <summary>
/// Entschlüsselt SCS-Spielstände (ScsC-Container: AES-256-CBC + zlib) und erkennt das Format.
/// </summary>
public static class SiiCodec
{
    private static readonly byte[] Key =
    {
        0x2a, 0x5f, 0xcb, 0x17, 0x91, 0xd2, 0x2f, 0xb6, 0x02, 0x45, 0xb3, 0xd8, 0x36, 0x9e, 0xd0, 0xb2,
        0xc2, 0x73, 0x71, 0x56, 0x3f, 0xbf, 0x1f, 0x3c, 0x9e, 0xdf, 0x6b, 0x11, 0x82, 0x5a, 0x5d, 0x0a,
    };

    private const int HeaderSize = 56; // Signatur(4) + HMAC(32) + IV(16) + Datengröße(4)

    public static bool IsEncrypted(byte[] data) =>
        data.Length >= HeaderSize && data[0] == 'S' && data[1] == 'c' && data[2] == 's' && data[3] == 'C';

    public static byte[] Decrypt(byte[] raw)
    {
        if (!IsEncrypted(raw)) return raw;

        var iv = raw.AsSpan(36, 16).ToArray();
        int length = (raw.Length - HeaderSize) / 16 * 16;

        using var aes = Aes.Create();
        aes.Key = Key;
        byte[] compressed = aes.DecryptCbc(raw.AsSpan(HeaderSize, length), iv, PaddingMode.None);

        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    public static SiiFormat Detect(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 'B' && data[1] == 'S' && data[2] == 'I' && data[3] == 'I')
            return SiiFormat.Binary;

        var head = Encoding.UTF8.GetString(data, 0, Math.Min(64, data.Length))
            .TrimStart('﻿', ' ', '\r', '\n', '\t');
        return head.StartsWith("SiiNunit", StringComparison.Ordinal) ? SiiFormat.Text : SiiFormat.Unknown;
    }
}

public static class SiiLoader
{
    public static ISiiDocument Load(byte[] raw, out bool wasEncrypted)
    {
        wasEncrypted = SiiCodec.IsEncrypted(raw);
        var data = SiiCodec.Decrypt(raw);
        return SiiCodec.Detect(data) switch
        {
            SiiFormat.Binary => BsiiDocument.Parse(data),
            SiiFormat.Text => TextSiiDocument.Parse(data),
            _ => throw new InvalidDataException("Unbekanntes Dateiformat."),
        };
    }
}

/// <summary>Kodierte Kurz-Strings („Tokens“) wie Städtenamen: Basis 38, max. 12 Zeichen.</summary>
public static class SiiToken
{
    private const string Chars = "0123456789abcdefghijklmnopqrstuvwxyz_";

    public static string Decode(ulong value)
    {
        var sb = new StringBuilder();
        while (value != 0)
        {
            int idx = (int)(value % 38);
            value /= 38;
            if (idx > 0) sb.Append(Chars[idx - 1]);
        }
        return sb.ToString();
    }

    public static ulong Encode(string token)
    {
        if (!IsValid(token)) throw new ArgumentException($"Ungültiger Token „{token}“.");
        ulong value = 0;
        for (int i = token.Length - 1; i >= 0; i--)
            value = value * 38 + (ulong)(Chars.IndexOf(token[i]) + 1);
        return value;
    }

    public static bool IsValid(string token) =>
        token.Length is > 0 and <= 12 && token.All(c => Chars.IndexOf(c) >= 0);
}
