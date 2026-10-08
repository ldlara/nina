using System.Security.Cryptography;
using System.Text.Json;

namespace Nina.Tracking.Sync;

/// <summary>Entrada do cursor para um bebê: sequência e "época" do vínculo (a revogação seguida de novo convite muda a época e invalida o cursor, INV-14).</summary>
internal sealed record CursorEntry(long Sequence, string Epoch);

/// <summary>Continuação de um snapshot em andamento: último tipo (rank no feed) e id entregues.</summary>
internal sealed record SnapshotPosition(int Rank, Guid Id);

internal sealed record CursorPayload(long IssuedAtUnix, IReadOnlyDictionary<Guid, CursorEntry> Babies, SnapshotPosition? Snapshot = null);

/// <summary>
/// Cursor opaco e assinado: <c>base64url(JSON) "." base64url(HMAC-SHA256(chave, JSON)[0..16])</c>, com
/// <c>{"v":1,"k":1,"t":emissão,"b":{"&lt;baby&gt;":[sequência,"época"]},"s":[rank,"&lt;id&gt;"]}</c>. O mapa por bebê é a forma de crescer para
/// pull multi-bebê sem mudar o contrato (opaco). O cliente só guarda e devolve (ADR-0003); qualquer alteração invalida o MAC.
/// </summary>
internal sealed class CursorCodec(byte[] secret)
{
    public const int MaxLength = 512;
    private const int MacBytes = 16;
    private const int Version = 1;
    private const int KeyId = 1;

    public static string EpochOf(Guid membershipId) => B64(SHA256.HashData(membershipId.ToByteArray())[..6]);

    public string Encode(CursorPayload payload)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteNumber("k", KeyId);
            w.WriteNumber("t", payload.IssuedAtUnix);
            w.WriteStartObject("b");
            foreach (var (baby, entry) in payload.Babies.OrderBy(x => x.Key))
            {
                w.WriteStartArray(baby.ToString("N"));
                w.WriteNumberValue(entry.Sequence);
                w.WriteStringValue(entry.Epoch);
                w.WriteEndArray();
            }

            w.WriteEndObject();
            if (payload.Snapshot is { } s)
            {
                w.WriteStartArray("s");
                w.WriteNumberValue(s.Rank);
                w.WriteStringValue(s.Id.ToString("N"));
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        var body = stream.ToArray();
        return B64(body) + "." + B64(Mac(body));
    }

    /// <summary>Devolve null se malformado, com MAC inválido ou de versão desconhecida (<c>reason=INVALID</c>).</summary>
    public CursorPayload? TryDecode(string? cursor)
    {
        try
        {
            if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength)
            {
                return null;
            }

            var parts = cursor.Split('.');
            if (parts.Length != 2)
            {
                return null;
            }

            var body = UnB64(parts[0]);
            var mac = UnB64(parts[1]);
            // Base64 canônico: os bits de preenchimento do último caractere não podem ser adulterados sem mudar o MAC.
            if (!string.Equals(B64(body), parts[0], StringComparison.Ordinal) || !string.Equals(B64(mac), parts[1], StringComparison.Ordinal))
            {
                return null;
            }

            if (!CryptographicOperations.FixedTimeEquals(mac, Mac(body)))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.GetProperty("v").GetInt32() != Version || root.GetProperty("k").GetInt32() != KeyId)
            {
                return null;
            }

            var babies = new Dictionary<Guid, CursorEntry>();
            foreach (var property in root.GetProperty("b").EnumerateObject())
            {
                babies[Guid.ParseExact(property.Name, "N")] = new CursorEntry(property.Value[0].GetInt64(), property.Value[1].GetString()!);
            }

            SnapshotPosition? snapshot = null;
            if (root.TryGetProperty("s", out var s))
            {
                snapshot = new SnapshotPosition(s[0].GetInt32(), Guid.ParseExact(s[1].GetString()!, "N"));
            }

            return new CursorPayload(root.GetProperty("t").GetInt64(), babies, snapshot);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private byte[] Mac(byte[] body) => HMACSHA256.HashData(secret, body)[..MacBytes];

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] UnB64(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
