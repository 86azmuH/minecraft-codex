package dev.minecraftcodex.client.companion;

import java.net.http.WebSocket;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;

public final class CompanionSession implements AutoCloseable {
    private final WebSocket socket;
    private final CompanionStatus status;
    private final CompletableFuture<Void> closed;
    private final AtomicBoolean closeRequested = new AtomicBoolean();
    private final AtomicBoolean socketClosed = new AtomicBoolean();
    private final AtomicBoolean taskRunning = new AtomicBoolean();
    private final ExecutorService taskExecutor = Executors.newSingleThreadExecutor(Thread.ofPlatform()
        .name("minecraft-codex-task", 0).daemon(true).factory());
    private final SnapshotListener listener;
    private volatile String currentTaskId;

    CompanionSession(WebSocket socket, CompanionStatus status, SnapshotListener listener) {
        this.socket = socket;
        this.status = status;
        this.listener = listener;
        this.closed = listener.closed();
    }

    public CompanionStatus status() {
        return status;
    }

    public CompletableFuture<Void> closed() {
        return closed;
    }

    public CompletableFuture<Void> runTask(String prompt, Consumer<String> onMessage) {
        if (!taskRunning.compareAndSet(false, true))
            return CompletableFuture.failedFuture(new IllegalStateException("a Codex task is already running"));
        return CompletableFuture.runAsync(() -> runTaskBlocking(prompt, onMessage), taskExecutor)
            .whenComplete((ignored, error) -> {
                currentTaskId = null;
                taskRunning.set(false);
                if (closeRequested.get()) closeSocket();
            });
    }

    private void runTaskBlocking(String prompt, Consumer<String> onMessage) {
        String requestId = CompanionProtocol.newRequestId();
        socket.sendText(CompanionProtocol.startTaskRequest(requestId, prompt), true).join();
        String taskId = null;
        List<CompanionProtocol.TaskMessage> earlyEvents = new ArrayList<>();
        while (true) {
            String json = listener.next().orTimeout(Duration.ofMinutes(10).toMillis(), TimeUnit.MILLISECONDS).join();
            CompanionProtocol.TaskMessage message = CompanionProtocol.parseTaskMessage(json);
            if (requestId.equals(message.requestId())) {
                if ("request.rejected".equals(message.type())) {
                    String reason = message.payloadString("reason");
                    if (reason == null) reason = message.payloadString("message");
                    throw new CompletionException(new IllegalStateException(
                        reason == null ? "Codex task was rejected" : reason));
                }
                if ("request.accepted".equals(message.type())) {
                    taskId = message.payloadString("taskId");
                    if (taskId == null) throw new CompletionException(new IllegalStateException("task ID was not returned"));
                    currentTaskId = taskId;
                    if (closeRequested.get()) sendCancellation(taskId);
                    for (CompanionProtocol.TaskMessage early : earlyEvents)
                        if (taskId.equals(early.taskId()) && handleTaskEvent(early, onMessage)) return;
                    earlyEvents.clear();
                    continue;
                }
            }
            if (message.taskId() == null) continue;
            if (taskId == null) {
                earlyEvents.add(message);
                continue;
            }
            if (taskId.equals(message.taskId()) && handleTaskEvent(message, onMessage)) return;
        }
    }

    private static boolean handleTaskEvent(CompanionProtocol.TaskMessage message, Consumer<String> onMessage) {
        return switch (message.type()) {
            case "message.completed" -> {
                String text = message.payloadString("text");
                if (text != null && !text.isBlank()) onMessage.accept(text);
                yield false;
            }
            case "task.completed" -> true;
            case "task.failed" -> {
                String category = message.payloadString("category");
                throw new CompletionException(new IllegalStateException(
                    "Codex task failed" + (category == null ? "" : ": " + category)));
            }
            default -> false;
        };
    }

    @Override
    public void close() {
        if (!closeRequested.compareAndSet(false, true)) return;
        String taskId = currentTaskId;
        if (taskId != null) sendCancellation(taskId);
        if (!taskRunning.get()) closeSocket();
        else CompletableFuture.delayedExecutor(5, TimeUnit.SECONDS).execute(this::abortIfStillOpen);
    }

    private void sendCancellation(String taskId) {
        if (!socketClosed.get())
            socket.sendText(CompanionProtocol.cancelTaskRequest(CompanionProtocol.newRequestId(), taskId), true);
    }

    private void closeSocket() {
        if (socketClosed.compareAndSet(false, true)) {
            taskExecutor.shutdown();
            socket.sendClose(WebSocket.NORMAL_CLOSURE, "Codex mode disabled");
        }
    }

    private void abortIfStillOpen() {
        if (socketClosed.compareAndSet(false, true)) {
            taskExecutor.shutdownNow();
            socket.abort();
        }
    }
}
