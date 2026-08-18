package dev.minecraftcodex.client.companion;

import java.net.http.WebSocket;
import java.nio.ByteBuffer;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.LinkedBlockingQueue;

final class SnapshotListener implements WebSocket.Listener {
    private final StringBuilder current = new StringBuilder();
    private final LinkedBlockingQueue<CompletableFuture<String>> waiters = new LinkedBlockingQueue<>();
    private final LinkedBlockingQueue<String> messages = new LinkedBlockingQueue<>();
    private final CompletableFuture<Void> closed = new CompletableFuture<>();

    CompletableFuture<Void> closed() {
        return closed;
    }

    CompletableFuture<String> next() {
        String message = messages.poll();
        if (message != null) return CompletableFuture.completedFuture(message);
        CompletableFuture<String> waiter = new CompletableFuture<>();
        waiters.add(waiter);
        waiter.whenComplete((ignored, error) -> waiters.remove(waiter));
        message = messages.poll();
        if (message != null && waiters.remove(waiter)) waiter.complete(message);
        return waiter;
    }

    @Override
    public void onOpen(WebSocket webSocket) {
        webSocket.request(1);
    }

    @Override
    public CompletionStage<?> onText(WebSocket webSocket, CharSequence data, boolean last) {
        current.append(data);
        if (last) deliver(current.toString());
        if (last) current.setLength(0);
        webSocket.request(1);
        return null;
    }

    @Override
    public CompletionStage<?> onBinary(WebSocket webSocket, ByteBuffer data, boolean last) {
        webSocket.abort();
        fail(new IllegalArgumentException("companion sent a binary WebSocket message"));
        return null;
    }

    @Override
    public void onError(WebSocket webSocket, Throwable error) {
        fail(error);
    }

    @Override
    public CompletionStage<?> onClose(WebSocket webSocket, int statusCode, String reason) {
        closed.complete(null);
        fail(new IllegalStateException("companion connection closed"));
        return null;
    }

    private void deliver(String message) {
        CompletableFuture<String> waiter;
        while ((waiter = waiters.poll()) != null)
            if (waiter.complete(message)) return;
        messages.add(message);
    }

    private void fail(Throwable error) {
        closed.completeExceptionally(error);
        CompletableFuture<String> waiter;
        while ((waiter = waiters.poll()) != null) waiter.completeExceptionally(error);
    }
}
