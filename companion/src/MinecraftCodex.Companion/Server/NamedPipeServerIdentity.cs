using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace MinecraftCodex.Companion.Server;

public static class NamedPipeServerIdentity
{
    public static void VerifyExecutable(NamedPipeClientStream pipe, string expectedExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Named-pipe server identity verification requires Windows.");
        if (!pipe.IsConnected)
            throw new InvalidOperationException("The bootstrap pipe is not connected.");

        var expected = Path.GetFullPath(expectedExecutablePath);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the bootstrap pipe server.");

        string actual;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            actual = process.MainModule?.FileName ??
                     throw new InvalidOperationException("The bootstrap pipe server executable is unavailable.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            throw new InvalidOperationException("The bootstrap pipe server identity could not be verified.", ex);
        }

        if (!string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bootstrap pipe server is not the expected companion executable.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}
