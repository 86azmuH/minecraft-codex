using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using MinecraftCodex.Companion.Security;
using MinecraftCodex.Companion.Tasks;

namespace MinecraftCodex.Companion.Codex;

public interface ICodexTaskAdapter
{
    Task RunAsync(TaskRun run, string prompt, string workingDirectory, TrustedPathPolicy pathPolicy,
        CancellationToken cancellationToken);
}

public sealed class CodexExecAdapter : ICodexTaskAdapter
{
    private readonly string executablePath;

    public CodexExecAdapter(string executablePath) => this.executablePath = executablePath;

    public async Task RunAsync(TaskRun run, string prompt, string workingDirectory,
        TrustedPathPolicy pathPolicy, CancellationToken cancellationToken)
    {
        var finalDecision = pathPolicy.EvaluateWorkingDirectory(workingDirectory);
        if (finalDecision.Status != PathStatus.Trusted || finalDecision.CanonicalPath is null)
        {
            run.Fail("workingDirectoryRejected");
            return;
        }

        using var process = new Process { StartInfo = CreateStartInfo(finalDecision.CanonicalPath) };
        try
        {
            process.Start();
            var stderrTask = DrainAsync(process.StandardError, cancellationToken);
            var stdoutTask = ReadStdoutAsync(run, process.StandardOutput, cancellationToken);
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellationToken);
            var outcome = await stdoutTask;
            await stderrTask;
            if (process.ExitCode == 0 && outcome.Completed && !outcome.Failed)
                run.Complete();
            else
                run.Fail(outcome.Failed ? "codexTurnFailed" : "codexExitedWithoutCompletion");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitExitAsync(process);
            run.Fail("cancelled", cancelled: true);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or JsonException)
        {
            TryKill(process);
            await AwaitExitAsync(process);
            run.Fail(ex.GetType().Name);
        }
    }

    private ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        var info = CodexProcessEncoding.Apply(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add("--json");
        info.ArgumentList.Add("--sandbox");
        info.ArgumentList.Add("read-only");
        info.ArgumentList.Add("--skip-git-repo-check");
        info.ArgumentList.Add("--cd");
        info.ArgumentList.Add(workingDirectory);
        info.ArgumentList.Add("-");
        return info;
    }

    private static async Task<CodexOutcome> ReadStdoutAsync(TaskRun run, StreamReader reader,
        CancellationToken cancellationToken)
    {
        var completed = false;
        var failed = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            switch (NormalizeLine(run, line))
            {
                case CodexObservation.Completed: completed = true; break;
                case CodexObservation.Failed: failed = true; break;
            }
        }
        return new(completed, failed);
    }

    private static CodexObservation NormalizeLine(TaskRun run, string line)
    {
        if (line.Length > 1_048_576) throw new JsonException("Codex event exceeded the limit.");
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type)) return CodexObservation.None;
        switch (type.GetString())
        {
            case "thread.started":
                run.Started(root.GetProperty("thread_id").GetString());
                return CodexObservation.None;
            case "item.completed" when root.TryGetProperty("item", out var item) &&
                                           item.TryGetProperty("type", out var itemType) &&
                                           itemType.GetString() == "agent_message":
                run.Message(item.GetProperty("text").GetString() ?? string.Empty);
                return CodexObservation.None;
            case "turn.completed":
                return CodexObservation.Completed;
            case "turn.failed":
            case "error":
                return CodexObservation.Failed;
            default:
                return CodexObservation.None;
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) > 0) { }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    private static async Task AwaitExitAsync(Process process)
    {
        try { await process.WaitForExitAsync(CancellationToken.None); }
        catch (InvalidOperationException) { }
    }

    private enum CodexObservation { None, Completed, Failed }
    private sealed record CodexOutcome(bool Completed, bool Failed);
}
