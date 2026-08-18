package dev.minecraftcodex.client;

import java.util.ArrayList;
import java.util.List;

final class ChatChunker {
    private ChatChunker() { }

    static List<String> split(String text, int maximumCharacters) {
        if (maximumCharacters < 1) throw new IllegalArgumentException("maximumCharacters must be positive");
        String remaining = text == null ? "" : text.strip();
        List<String> chunks = new ArrayList<>();
        while (!remaining.isEmpty()) {
            if (remaining.length() <= maximumCharacters) {
                chunks.add(remaining);
                break;
            }
            int split = bestSplit(remaining, maximumCharacters);
            chunks.add(remaining.substring(0, split).stripTrailing());
            remaining = remaining.substring(split).stripLeading();
        }
        return chunks;
    }

    private static int bestSplit(String value, int maximumCharacters) {
        int newline = value.lastIndexOf('\n', maximumCharacters);
        if (newline > maximumCharacters / 2) return newline + 1;
        int whitespace = maximumCharacters;
        while (whitespace > 0 && !Character.isWhitespace(value.charAt(whitespace))) whitespace--;
        return whitespace > maximumCharacters / 2 ? whitespace + 1 : maximumCharacters;
    }
}
