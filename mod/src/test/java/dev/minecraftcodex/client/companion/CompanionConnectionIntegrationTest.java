package dev.minecraftcodex.client.companion;

import org.junit.jupiter.api.Test;

import java.time.Duration;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

final class CompanionConnectionIntegrationTest {
    @Test
    void launchesAuthenticatesAndRequestsSnapshotFromRealCompanion() {
        assumeTrue(Boolean.getBoolean("minecraftcodex.integration"));
        CompanionStatus status = CompanionConnectionService.fromEnvironment().refresh()
            .orTimeout(Duration.ofSeconds(30).toMillis(), java.util.concurrent.TimeUnit.MILLISECONDS)
            .join();
        assertEquals(CompanionStatus.State.CONNECTED, status.state(), status.detail());
        assertEquals(1, status.protocolVersion());
    }

    @Test
    void startsTaskAndReceivesPrivateMessageEventFromRealCompanion() {
        assumeTrue(Boolean.getBoolean("minecraftcodex.integration"));
        CompanionSession session = CompanionConnectionService.fromEnvironment().openSession()
            .orTimeout(Duration.ofSeconds(30).toMillis(), java.util.concurrent.TimeUnit.MILLISECONDS)
            .join();
        try {
            List<String> messages = new CopyOnWriteArrayList<>();
            session.runTask("Reply with exactly CHAT_SLICE_OK and nothing else.", messages::add)
                .orTimeout(Duration.ofMinutes(3).toMillis(), java.util.concurrent.TimeUnit.MILLISECONDS)
                .join();
            assertTrue(messages.stream().anyMatch(message -> message.contains("CHAT_SLICE_OK")), messages.toString());
        } finally {
            session.close();
        }
    }
}
