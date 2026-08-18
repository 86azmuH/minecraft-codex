package dev.minecraftcodex.client;

public final class CodexModeController {
    public enum State { DISABLED, CONNECTING, ENABLED }
    public enum Completion { STALE, FAILED, ENABLED }

    private State state = State.DISABLED;
    private long generation;

    public synchronized State state() {
        return state;
    }

    public synchronized long beginEnable() {
        if (state != State.DISABLED) return -1;
        state = State.CONNECTING;
        return ++generation;
    }

    public synchronized Completion finishEnable(long attempt, boolean connected) {
        if (state != State.CONNECTING || attempt != generation) return Completion.STALE;
        state = connected ? State.ENABLED : State.DISABLED;
        return connected ? Completion.ENABLED : Completion.FAILED;
    }

    public synchronized boolean disable() {
        boolean changed = state != State.DISABLED;
        generation++;
        state = State.DISABLED;
        return changed;
    }

    public synchronized boolean isEnabled() {
        return state == State.ENABLED;
    }
}
