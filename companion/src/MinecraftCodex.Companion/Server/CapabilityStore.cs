using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MinecraftCodex.Companion.Server;

public sealed class CapabilityStore
{
    private const int MaximumOutstanding = 16;
    private readonly ConcurrentDictionary<string, DateTimeOffset> capabilities = new(StringComparer.Ordinal);

    public string? Mint(DateTimeOffset now, TimeSpan lifetime)
    {
        Prune(now);
        if (capabilities.Count >= MaximumOutstanding) return null;
        var capability = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        capabilities[Digest(capability)] = now.Add(lifetime);
        return capability;
    }

    public bool TryConsume(string capability, DateTimeOffset now)
    {
        var digest = Digest(capability);
        if (!capabilities.TryRemove(digest, out var expiresAt)) return false;
        return now <= expiresAt;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var item in capabilities)
            if (item.Value < now) capabilities.TryRemove(item.Key, out _);
    }

    private static string Digest(string capability) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capability)));
}
