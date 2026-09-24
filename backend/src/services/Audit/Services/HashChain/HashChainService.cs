using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Custodian.Audit.Services.HashChain;

public sealed class HashChainService : IHashChainService
{
    public string GenesisHash { get; } = new string('0', 64);

    public string ComputeEventHash(EventHashInput input)
    {
        var canonical = Canonicalize(input);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public bool VerifyEventHash(EventHashInput input, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var computed = ComputeEventHash(input);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(expectedHash)
        );
    }

    /// <summary>
    /// Produces a byte-for-byte reproducible JSON representation of the hash
    /// input: keys sorted ordinally (recursively, including inside the payload),
    /// no whitespace, no BOM, invariant number formatting, UTC timestamp in "O".
    /// Any change to this method's output invalidates every existing hash, so
    /// treat it as part of the wire contract.
    /// </summary> 
    private static string Canonicalize(EventHashInput input)
    {
        var payloadNode = string.IsNullOrWhiteSpace(input.Payload)
            ? new JsonObject()
            : JsonNode.Parse(input.Payload) ?? new JsonObject();
        
        var canonicalPayload = SortRecursively(payloadNode);

        var root = new JsonObject
        {
            ["eventId"]      = input.EventId.ToString("D"),
            ["engagementId"] = input.EngagementId.ToString("D"),
            ["tenantId"]     = input.TenantId.ToString("D"),
            ["actor"]        = input.Actor,
            ["type"]         = input.Type,
            ["timestamp"]    = DateTime.SpecifyKind(input.Timestamp, DateTimeKind.Utc).ToString("O"),
            ["payload"]      = canonicalPayload,
            ["previousHash"] = input.PreviousHash,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false});
    }
    private static JsonNode SortRecursively(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var sorted = new JsonObject();
                foreach (var kvp in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    sorted[kvp.Key] = kvp.Value is null ? null : SortRecursively(kvp.Value);
                }
                return sorted;
            }
            case JsonArray arr:
            {
                var copy = new JsonArray();
                foreach (var item in arr)
                {
                    copy.Add(item is null ? null : SortRecursively(item));
                }
                return copy;
            }
            default:
                return node.DeepClone();
        }
    }
}