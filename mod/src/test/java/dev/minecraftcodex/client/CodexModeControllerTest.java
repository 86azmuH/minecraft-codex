package dev.minecraftcodex.client;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.assertEquals;

final class CodexModeControllerTest {
    @Test
    void enablesOnlyAfterSuccessfulConnection() {
        CodexModeController mode = new CodexModeController();
        long attempt = mode.beginEnable();
        assertEquals(CodexModeController.State.CONNECTING, mode.state());
        assertEquals(CodexModeController.Completion.ENABLED, mode.finishEnable(attempt, true));
        assertEquals(CodexModeController.State.ENABLED, mode.state());
    }

    @Test
    void failedConnectionReturnsToDisabled() {
        CodexModeController mode = new CodexModeController();
        long attempt = mode.beginEnable();
        assertEquals(CodexModeController.Completion.FAILED, mode.finishEnable(attempt, false));
        assertEquals(CodexModeController.State.DISABLED, mode.state());
    }

    @Test
    void resetInvalidatesAnInFlightConnection() {
        CodexModeController mode = new CodexModeController();
        long attempt = mode.beginEnable();
        mode.disable();
        assertEquals(CodexModeController.Completion.STALE, mode.finishEnable(attempt, true));
        assertEquals(CodexModeController.State.DISABLED, mode.state());
    }

    @Test
    void repeatedEnableDoesNotStartAnotherAttempt() {
        CodexModeController mode = new CodexModeController();
        assertTrue(mode.beginEnable() > 0);
        assertEquals(-1, mode.beginEnable());
    }

    @Test
    void oldCompletionCannotAffectANewerAttempt() {
        CodexModeController mode = new CodexModeController();
        long oldAttempt = mode.beginEnable();
        mode.disable();
        long newAttempt = mode.beginEnable();
        assertEquals(CodexModeController.Completion.STALE, mode.finishEnable(oldAttempt, false));
        assertEquals(CodexModeController.State.CONNECTING, mode.state());
        assertEquals(CodexModeController.Completion.ENABLED, mode.finishEnable(newAttempt, true));
    }
}
