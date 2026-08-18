using System.Diagnostics;
using System.Text;

namespace MinecraftCodex.Companion.Codex;

internal static class CodexProcessEncoding
{
    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static ProcessStartInfo Apply(ProcessStartInfo startInfo)
    {
        if (startInfo.RedirectStandardInput) startInfo.StandardInputEncoding = Utf8;
        if (startInfo.RedirectStandardOutput) startInfo.StandardOutputEncoding = Utf8;
        if (startInfo.RedirectStandardError) startInfo.StandardErrorEncoding = Utf8;
        return startInfo;
    }
}
