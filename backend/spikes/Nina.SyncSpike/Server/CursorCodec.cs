using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nina.SyncSpike.Server;

/// <summary>Entrada do cursor para um bebê: sequência + "época" do vínculo (invalida cursor após revogação).</summary>
public sealed record CursorEntry(long Seq, string Epoch);

/// <summary>Posição de continuação do snapshot (rank do tipo, id da última entidade enviada).</summary>
public sealed record SnapshotPosition(int Rank, Guid Id);

public sealed record CursorPayload(
    long IssuedAtUnix,
    IReadOnlyDictionary<Guid, CursorEntry> Babies,
    SnapshotPosition? Snapshot = null);

/// <summary>
/// Cursor opaco: base64url(JSON compacto) + "." + base64url(HMAC-SHA256 truncado a 128 bits).
/// JSON: {"v":1,"t":iat,"b":{"&lt;baby&gt;":[seq,"epoca"]},"s":[rank,"&lt;id&gt;"]}. O cliente nunca interpreta.
/// </summary>
public sealed class CursorCodec(byte[] secret)
{
    private const int MacBytes = 16;

    public static string EpochOf(Guid membershipId)
    {
        var h = SHA256.HashData(membershipId.ToByteArray());
        return Convert.ToBase64String(h, 0, 6).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public string Encode(CursorPayload p)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteNumber("t", p.IssuedAtUnix);
            w.WriteStartObject("b");
            foreach (var (baby, e) in p.Babies.OrderBy(x => x.Key))
            {
                w.WriteStartArray(baby.ToString("N"));
                w.WriteNumberValue(e.Seq);
                w.WriteStringValue(e.Epoch);
                w.WriteEndArray();
            }
            w.WriteEndObject();
            if (p.Snapshot is { } s)
            {
                w.WriteStartArray("s");
                w.WriteNumberValue(s.Rank);
                w.WriteStringValue(s.Id.ToString("N"));
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        var body = ms.ToArray();
        return B64(body) + "." + B64(Mac(body));
    }

    /// <summary>Devolve null se malformado ou com MAC inválido (reason INVALID).</summary>
    public CursorPayload? TryDecode(string? cursor)
    {
        try
        {
            if (string.IsNullOrEmpty(cursor) || cursor.Length > 512) return null;
            var parts = cursor.Split('.');
            if (parts.Length != 2) return null;
            var body = UnB64(parts[0]);
            var mac = UnB64(parts[1]);
            if (B64(body) != parts[0] || B64(mac) != parts[1]) return null;       // base64 canônico (sem bits de preenchimento adulteráveis)
            if (!CryptographicOperations.FixedTimeEquals(mac, Mac(body))) return null;
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.GetProperty("v").GetInt32() != 1) return null;
            var babies = new Dictionary<Guid, CursorEntry>();
            foreach (var prop in root.GetProperty("b").EnumerateObject())
                babies[Guid.ParseExact(prop.Name, "N")] = new CursorEntry(prop.Value[0].GetInt64(), prop.Value[1].GetString()!);
            SnapshotPosition? snap = null;
            if (root.TryGetProperty("s", out var s))
                snap = new SnapshotPosition(s[0].GetInt32(), Guid.ParseExact(s[1].GetString()!, "N"));
            return new CursorPayload(root.GetProperty("t").GetInt64(), babies, snap);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    private byte[] Mac(byte[] body) => HMACSHA256.HashData(secret, body)[..MacBytes];

    private static string B64(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] UnB64(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        return Convert.FromBase64String(t);
    }

    public static string Describe(string cursor) => Encoding.UTF8.GetString(UnB64(cursor.Split('.')[0]));
}
