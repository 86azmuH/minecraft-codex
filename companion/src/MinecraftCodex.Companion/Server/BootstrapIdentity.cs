using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MinecraftCodex.Companion.Server;

public static class BootstrapIdentity
{
    public static string CurrentUserPipeName()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Minecraft Codex companion currently supports Windows only.");
        var sid = WindowsIdentity.GetCurrent().User?.Value ??
                  throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        return $"minecraft-codex-{Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}
