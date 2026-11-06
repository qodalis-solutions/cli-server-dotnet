namespace Qodalis.Cli.Abstractions.Events;

/// <summary>
/// Event type patterns, as web-cli's <c>matchesEventPattern</c>: <c>*</c> matches every type, <c>ns:*</c> every type
/// that starts with <c>ns:</c>, anything else only the type itself.
/// </summary>
public static class CliEventPattern
{
    /// <summary>Whether <paramref name="type"/> matches <paramref name="pattern"/>.</summary>
    public static bool Matches(string type, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith(":*", StringComparison.Ordinal))
            return type.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return type == pattern;
    }
}
