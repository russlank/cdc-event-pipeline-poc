using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace CdcPay.Common;

/// <summary>
/// Deterministic event ids: name-based UUIDs, version 5 (RFC 9562). The same name always
/// yields the same id, so replays and redeliveries of a source change carry the same id.
/// </summary>
public static class EventIds
{
    private static readonly Guid UrlNamespace = new("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

    /// <summary>Namespace of every event id in this solution: UUIDv5("urn:cdc-pay-poc:events") in the URL namespace.</summary>
    public static Guid Namespace { get; } = CreateVersion5(UrlNamespace, "urn:cdc-pay-poc:events");

    /// <summary>The event id for a name built from the source position or content.</summary>
    public static Guid FromName(string name) => CreateVersion5(Namespace, name);

    [SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms",
        Justification = "UUID version 5 is defined over SHA-1; the hash identifies, it does not protect.")]
    public static Guid CreateVersion5(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] buffer = new byte[16 + nameBytes.Length];
        _ = namespaceId.TryWriteBytes(buffer.AsSpan(0, 16), bigEndian: true, out _);
        nameBytes.CopyTo(buffer, 16);

        Span<byte> hash = stackalloc byte[20];
        _ = SHA1.HashData(buffer, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
