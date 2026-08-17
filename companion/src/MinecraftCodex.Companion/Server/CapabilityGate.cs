using System.Security.Cryptography;
using System.Text;

namespace MinecraftCodex.Companion.Server;

public sealed class CapabilityGate
{
    private readonly byte[] expectedDigest;
    private readonly DateTimeOffset expiresAt;
    private int consumed;

    public CapabilityGate(string capability, DateTimeOffset expiresAt)
    {
        expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(capability));
        this.expiresAt = expiresAt;
    }

    public bool TryConsume(string capability, DateTimeOffset now)
    {
        if (now > expiresAt) return false;
        var actualDigest = SHA256.HashData(Encoding.UTF8.GetBytes(capability));
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest)) return false;
        return Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    }
}
