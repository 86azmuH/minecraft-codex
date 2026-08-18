using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using MinecraftCodex.Companion.Codex;
using MinecraftCodex.Companion.Protocol;
using MinecraftCodex.Companion.Security;

namespace MinecraftCodex.Companion.Tasks;

public sealed class TaskRegistry
{
    private readonly ICodexTaskAdapter adapter;
    private readonly TrustedPathPolicy pathPolicy;
    private readonly string defaultWorkingDirectory;
    private readonly ConcurrentDictionary<string, TaskRun> tasks = new();
    private readonly ConcurrentDictionary<string, RequestRegistration> requestTasks = new();
    private readonly ConcurrentDictionary<string, Task> executions = new();
    private readonly object lifecycleGate = new();
    private readonly object subscriberLock = new();
    private readonly List<Channel<ProtocolMessage>> subscribers = [];
    private int stopping;
    private int activeCount;

    public event Action<int>? ActiveCountChanged;
    public int ActiveCount => Volatile.Read(ref activeCount);

    public TaskRegistry(ICodexTaskAdapter adapter, TrustedPathPolicy pathPolicy, string defaultWorkingDirectory)
    {
        this.adapter = adapter;
        this.pathPolicy = pathPolicy;
        this.defaultWorkingDirectory = defaultWorkingDirectory;
    }

    public ChannelReader<ProtocolMessage> Subscribe(out Action unsubscribe)
    {
        var channel = Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        lock (subscriberLock) subscribers.Add(channel);
        unsubscribe = () => { lock (subscriberLock) subscribers.Remove(channel); };
        return channel.Reader;
    }

    public IReadOnlyList<TaskSnapshot> Snapshot() => tasks.Values
        .Select(task => task.Snapshot())
        .OrderBy(snapshot => snapshot.TaskId, StringComparer.Ordinal)
        .ToArray();

    public (bool Accepted, string Message, string? TaskId) Start(string requestId, StartTaskPayload payload)
    {
        lock (lifecycleGate)
        {
            if (stopping != 0)
                return (false, "companion is stopping", null);
            if (requestId.Length > 128)
                return (false, "request ID is too long", null);
            if (string.IsNullOrWhiteSpace(payload.Prompt) || payload.Prompt.Length > 32_768)
                return (false, "prompt must contain between 1 and 32768 characters", null);

            var decision = pathPolicy.EvaluateWorkingDirectory(payload.WorkingDirectory ?? defaultWorkingDirectory);
            if (decision.Status != PathStatus.Trusted || decision.CanonicalPath is null)
                return (false, decision.Reason, null);

            var fingerprint = Fingerprint(payload.Prompt, decision.CanonicalPath);
            if (requestTasks.TryGetValue(requestId, out var existing))
                return existing.Fingerprint == fingerprint
                    ? (true, "request already accepted", existing.TaskId)
                    : (false, "request ID was reused with different content", null);
            if (tasks.Count >= 8)
                return (false, "companion task limit reached", null);

            var taskId = Guid.NewGuid().ToString("N");
            requestTasks[requestId] = new(taskId, fingerprint);
            var run = new TaskRun(taskId, Publish);
            tasks[taskId] = run;
            var newActiveCount = Interlocked.Increment(ref activeCount);
            ActiveCountChanged?.Invoke(newActiveCount);
            var execution = Task.Run(() => RunAdapterAsync(run, payload.Prompt, decision.CanonicalPath), CancellationToken.None);
            executions[taskId] = execution;
            _ = execution.ContinueWith(ignored => executions.TryRemove(taskId, out _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return (true, "task accepted", taskId);
        }
    }

    private async Task RunAdapterAsync(TaskRun run, string prompt, string workingDirectory)
    {
        try { await adapter.RunAsync(run, prompt, workingDirectory, pathPolicy, run.CancellationToken); }
        catch (OperationCanceledException) { run.Fail("cancelled", cancelled: true); }
        catch (Exception) { run.Fail("unexpectedAdapterFailure"); }
        finally
        {
            var newActiveCount = Interlocked.Decrement(ref activeCount);
            ActiveCountChanged?.Invoke(newActiveCount);
        }
    }

    public bool Cancel(string taskId) => tasks.TryGetValue(taskId, out var task) && task.Cancel();

    public void CancelAll()
    {
        foreach (var task in tasks.Values) task.Cancel();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Task[] active;
        lock (lifecycleGate)
        {
            stopping = 1;
            CancelAll();
            active = executions.Values.ToArray();
        }
        if (active.Length > 0) await Task.WhenAll(active).WaitAsync(cancellationToken);
    }

    private void Publish(ProtocolMessage message)
    {
        lock (subscriberLock)
        {
            foreach (var subscriber in subscribers.ToArray())
            {
                if (subscriber.Writer.TryWrite(message)) continue;
                subscriber.Writer.TryComplete();
                subscribers.Remove(subscriber);
            }
        }
    }

    private static string Fingerprint(string prompt, string workingDirectory)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(workingDirectory + "\0" + prompt));
        return Convert.ToHexString(bytes);
    }

    private sealed record RequestRegistration(string TaskId, string Fingerprint);
}

public enum CompanionTaskState { Starting, Running, Completed, Failed, Cancelled }
public sealed record TaskSnapshot(string TaskId, CompanionTaskState State, string? ThreadId, long LastSequence,
    bool HistoryTruncated, IReadOnlyList<ProtocolMessage> Events);

public sealed class TaskRun
{
    private const int MaximumHistory = 512;
    private readonly Action<ProtocolMessage> publish;
    private readonly object sync = new();
    private readonly Queue<ProtocolMessage> history = new();
    private readonly CancellationTokenSource cancellation = new();
    private long sequence;
    private bool historyTruncated;
    private CompanionTaskState state = CompanionTaskState.Starting;
    private string? threadId;

    public TaskRun(string taskId, Action<ProtocolMessage> publish)
    {
        TaskId = taskId;
        this.publish = publish;
    }

    public string TaskId { get; }
    public CancellationToken CancellationToken => cancellation.Token;

    public void Emit(string type, object? payload = null)
    {
        ProtocolMessage message;
        lock (sync)
        {
            message = ProtocolMessage.Event(type, TaskId, ++sequence, payload);
            history.Enqueue(message);
            while (history.Count > MaximumHistory)
            {
                history.Dequeue();
                historyTruncated = true;
            }
        }
        publish(message);
    }

    public void Started(string? officialThreadId)
    {
        lock (sync)
        {
            if (IsTerminal(state)) return;
            state = CompanionTaskState.Running;
            threadId = officialThreadId;
        }
        Emit("task.started", new { threadId = officialThreadId });
    }

    public void Message(string text) => Emit("message.completed", new { text });

    public void Complete()
    {
        lock (sync)
        {
            if (IsTerminal(state)) return;
            state = CompanionTaskState.Completed;
        }
        Emit("task.completed");
    }

    public void Fail(string category, bool cancelled = false)
    {
        lock (sync)
        {
            if (IsTerminal(state)) return;
            state = cancelled ? CompanionTaskState.Cancelled : CompanionTaskState.Failed;
        }
        Emit("task.failed", new { category });
    }

    public bool Cancel()
    {
        lock (sync)
        {
            if (IsTerminal(state)) return false;
            cancellation.Cancel();
            return true;
        }
    }

    public TaskSnapshot Snapshot()
    {
        lock (sync) return new(TaskId, state, threadId, sequence, historyTruncated, history.ToArray());
    }

    private static bool IsTerminal(CompanionTaskState value) =>
        value is CompanionTaskState.Completed or CompanionTaskState.Failed or CompanionTaskState.Cancelled;
}
