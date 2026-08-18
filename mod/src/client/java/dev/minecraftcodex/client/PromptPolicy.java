package dev.minecraftcodex.client;

final class PromptPolicy {
    private static final String CONCISE_GUIDANCE =
        "Answer in a few concise sentences unless more detail is necessary.";

    private PromptPolicy() { }

    static String forCodex(String userPrompt) {
        return CONCISE_GUIDANCE + "\n\nUser prompt:\n" + userPrompt;
    }
}
