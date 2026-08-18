package dev.minecraftcodex.client.companion;

public record CompanionStatus(State state, int protocolVersion, int retainedTasks, String detail) {
    public enum State { CONNECTED, NOT_CONFIGURED, UNAVAILABLE }

    public static CompanionStatus connected(int protocolVersion, int retainedTasks) {
        return new CompanionStatus(State.CONNECTED, protocolVersion, retainedTasks, "connected");
    }

    public static CompanionStatus notConfigured() {
        return new CompanionStatus(State.NOT_CONFIGURED, 0, 0, "companion executable is not configured");
    }

    public static CompanionStatus unavailable(String detail) {
        return new CompanionStatus(State.UNAVAILABLE, 0, 0, sanitize(detail));
    }

    private static String sanitize(String detail) {
        if (detail == null || detail.isBlank()) return "unknown error";
        String firstLine = detail.lines().findFirst().orElse("unknown error").trim();
        return firstLine.length() <= 160 ? firstLine : firstLine.substring(0, 160);
    }
}
