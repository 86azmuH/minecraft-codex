namespace MinecraftCodex.Companion.Server;

public sealed class CompanionInstanceLease : IDisposable
{
    private readonly FileStream? lockStream;

    private CompanionInstanceLease(FileStream? lockStream)
    {
        this.lockStream = lockStream;
    }

    public static CompanionInstanceLease TryAcquire(string pipeName, string? runtimeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        runtimeDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MinecraftCodex", "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        var lockPath = Path.Combine(runtimeDirectory, $"{pipeName}.lock");
        try
        {
            return new CompanionInstanceLease(new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
        }
        catch (IOException)
        {
            return new CompanionInstanceLease(null);
        }
    }

    public bool IsOwner => lockStream is not null;

    public void Dispose() => lockStream?.Dispose();
}
