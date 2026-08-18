package dev.minecraftcodex.client.companion;

import com.sun.jna.Memory;
import com.sun.jna.Native;
import com.sun.jna.Pointer;
import com.sun.jna.WString;
import com.sun.jna.platform.win32.WinBase;
import com.sun.jna.platform.win32.WinNT;
import com.sun.jna.ptr.IntByReference;
import com.sun.jna.win32.StdCallLibrary;
import com.sun.jna.win32.W32APIOptions;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

final class WindowsBootstrapPipe {
    private static final Kernel32Ex KERNEL32 = Native.load("kernel32", Kernel32Ex.class, W32APIOptions.DEFAULT_OPTIONS);
    private static final int MAX_REPLY_BYTES = 16 * 1024;

    private WindowsBootstrapPipe() { }

    static CompanionProtocol.Capability request(String pipeName, Path expectedExecutable, String request) throws IOException {
        String path = "\\\\.\\pipe\\" + pipeName;
        WinNT.HANDLE pipe = KERNEL32.CreateFile(new WString(path),
            WinNT.GENERIC_READ | WinNT.GENERIC_WRITE, 0, null, WinNT.OPEN_EXISTING, 0, null);
        if (WinBase.INVALID_HANDLE_VALUE.equals(pipe)) throw win32("could not open companion bootstrap pipe");
        try {
            verifyServer(pipe, expectedExecutable);
            byte[] bytes = request.getBytes(StandardCharsets.UTF_8);
            Memory outgoing = new Memory(bytes.length);
            outgoing.write(0, bytes, 0, bytes.length);
            IntByReference written = new IntByReference();
            if (!KERNEL32.WriteFile(pipe, outgoing, bytes.length, written, null) || written.getValue() != bytes.length)
                throw win32("could not write companion bootstrap request");

            Memory incoming = new Memory(MAX_REPLY_BYTES);
            IntByReference read = new IntByReference();
            if (!KERNEL32.ReadFile(pipe, incoming, MAX_REPLY_BYTES, read, null) || read.getValue() <= 0)
                throw win32("could not read companion bootstrap reply");
            String reply = new String(incoming.getByteArray(0, read.getValue()), StandardCharsets.UTF_8).strip();
            return CompanionProtocol.parseCapability(reply);
        } finally {
            KERNEL32.CloseHandle(pipe);
        }
    }

    private static void verifyServer(WinNT.HANDLE pipe, Path expectedExecutable) throws IOException {
        IntByReference pid = new IntByReference();
        if (!KERNEL32.GetNamedPipeServerProcessId(pipe, pid)) throw win32("could not identify bootstrap pipe server");
        WinNT.HANDLE process = KERNEL32.OpenProcess(WinNT.PROCESS_QUERY_LIMITED_INFORMATION, false, pid.getValue());
        if (process == null || WinBase.INVALID_HANDLE_VALUE.equals(process)) throw win32("could not inspect bootstrap pipe server");
        try {
            char[] buffer = new char[32768];
            IntByReference length = new IntByReference(buffer.length);
            if (!KERNEL32.QueryFullProcessImageName(process, 0, buffer, length)) throw win32("could not resolve bootstrap pipe server");
            Path actual = Path.of(new String(buffer, 0, length.getValue())).toRealPath();
            Path expected = expectedExecutable.toRealPath();
            if (!Files.isSameFile(actual, expected)) throw new IOException("bootstrap pipe server is not the configured companion executable");
        } finally {
            KERNEL32.CloseHandle(process);
        }
    }

    private static IOException win32(String message) {
        return new IOException(message + " (Windows error " + KERNEL32.GetLastError() + ")");
    }

    private interface Kernel32Ex extends StdCallLibrary {
        WinNT.HANDLE CreateFile(WString name, int access, int shareMode, WinBase.SECURITY_ATTRIBUTES security,
                                int creationDisposition, int flags, WinNT.HANDLE template);
        boolean ReadFile(WinNT.HANDLE file, Pointer buffer, int bytesToRead, IntByReference bytesRead,
                         WinBase.OVERLAPPED overlapped);
        boolean WriteFile(WinNT.HANDLE file, Pointer buffer, int bytesToWrite, IntByReference bytesWritten,
                          WinBase.OVERLAPPED overlapped);
        boolean GetNamedPipeServerProcessId(WinNT.HANDLE pipe, IntByReference serverProcessId);
        WinNT.HANDLE OpenProcess(int desiredAccess, boolean inheritHandle, int processId);
        boolean QueryFullProcessImageName(WinNT.HANDLE process, int flags, char[] executableName,
                                          IntByReference size);
        boolean CloseHandle(WinNT.HANDLE handle);
        int GetLastError();
    }
}
