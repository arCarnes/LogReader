namespace LogReader.Core;

public static class WholeWordMatcher
{
    public static bool IsWholeWordMatch(string text, int matchStart, int matchLength)
    {
        ArgumentNullException.ThrowIfNull(text);

        var wordStart = matchStart == 0 || !IsWordCharacter(text[matchStart - 1]);
        var wordEnd = matchStart + matchLength >= text.Length || !IsWordCharacter(text[matchStart + matchLength]);
        return wordStart && wordEnd;
    }

    private static bool IsWordCharacter(char c)
        => char.IsLetterOrDigit(c) || c == '_';
}
