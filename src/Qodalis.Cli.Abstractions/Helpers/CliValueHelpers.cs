using System.Text;

namespace Qodalis.Cli.Abstractions.Helpers;

/// <summary>
/// Helpers for the positional value of a <see cref="CliProcessCommand"/>. The browser
/// sends the value as typed, quotes included (<c>echo "hello world"</c> has the value
/// <c>"hello world"</c>); its arguments arrive already parsed.
/// </summary>
public static class CliValueHelpers
{
    /// <summary>
    /// The arguments as a shell's echo prints them, as the browser CLI's own <c>echo</c>
    /// does: quotes removed, the words joined by one space, spacing inside quotes kept
    /// (<c>"a" "b"</c> becomes <c>a b</c>). A quote without a closing one is text.
    /// </summary>
    public static string JoinShellWords(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var words = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var end = ch is '"' or '\'' ? text.IndexOf(ch, i + 1) : -1;
            if (end != -1)
            {
                current.Append(text, i + 1, end - i - 1);
                inWord = true;
                i = end;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (inWord) words.Add(current.ToString());
                current.Clear();
                inWord = false;
            }
            else
            {
                current.Append(ch);
                inWord = true;
            }
        }
        if (inWord) words.Add(current.ToString());
        return string.Join(' ', words);
    }
}
