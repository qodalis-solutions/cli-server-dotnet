using System.Text;

namespace Qodalis.Cli.Abstractions.Helpers;

/// <summary>
/// Helpers for the positional value of a <see cref="CliProcessCommand"/>. The browser
/// sends the value as typed, quotes included (<c>echo "hello world"</c> has the value
/// <c>"hello world"</c>), and newer browsers also send the positional arguments already
/// split (<see cref="CliProcessCommand.Positionals"/>).
/// </summary>
public static class CliValueHelpers
{
    /// <summary>
    /// The positional arguments of a command: <see cref="CliProcessCommand.Positionals"/> when the
    /// browser sent them, otherwise <see cref="CliProcessCommand.Value"/> split with
    /// <see cref="SplitShellWords"/> (older browsers).
    /// </summary>
    public static IReadOnlyList<string> GetPositionals(CliProcessCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Positionals ?? SplitShellWords(command.Value);
    }

    /// <summary>
    /// The positional arguments as one text, quotes removed and joined by one space
    /// (<c>base64 encode "a b"</c> gives <c>a b</c>). Empty when there are none.
    /// </summary>
    public static string GetPositionalText(CliProcessCommand command) =>
        string.Join(' ', GetPositionals(command));

    /// <summary>
    /// Splits text into words like a shell: whitespace separates words, quotes are removed
    /// (<c>"a b"</c> and <c>'a b'</c> are one word <c>a b</c>), a backslash outside quotes
    /// escapes the next character (<c>my\ file</c> is <c>my file</c>), inside double quotes
    /// <c>\"</c> and <c>\\</c> are escapes, and single quotes are literal. A quote without a
    /// closing one is text.
    /// </summary>
    public static List<string> SplitShellWords(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text)) return words;
        var current = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
                inWord = true;
            }
            else if (ch == '\'' && text.IndexOf('\'', i + 1) is var end && end != -1)
            {
                current.Append(text, i + 1, end - i - 1);
                inWord = true;
                i = end;
            }
            else if (ch == '"' && FindClosingDoubleQuote(text, i + 1) is var close && close != -1)
            {
                for (var j = i + 1; j < close; j++)
                {
                    if (text[j] == '\\' && j + 1 < close && text[j + 1] is '"' or '\\') j++;
                    current.Append(text[j]);
                }
                inWord = true;
                i = close;
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
        return words;
    }

    private static int FindClosingDoubleQuote(string text, int start)
    {
        for (var j = start; j < text.Length; j++)
        {
            if (text[j] == '\\' && j + 1 < text.Length && text[j + 1] is '"' or '\\') j++;
            else if (text[j] == '"') return j;
        }
        return -1;
    }

    /// <summary>
    /// The arguments as a shell's echo prints them, as the browser CLI's own <c>echo</c>
    /// does: the words of <see cref="SplitShellWords"/> joined by one space, spacing inside
    /// quotes kept (<c>"a" "b"</c> becomes <c>a b</c>).
    /// </summary>
    public static string JoinShellWords(string? text) => string.Join(' ', SplitShellWords(text));

    /// <summary>
    /// Quotes one argument so the browser CLI parser reads it back unchanged: as is when it has
    /// no whitespace, quotes, backslashes or <c>=</c>; in double quotes when it has no <c>"</c>;
    /// in single quotes when it has no <c>'</c>; otherwise in double quotes with <c>\"</c> escapes.
    /// Backslashes inside double quotes are written as <c>\\</c>.
    /// </summary>
    public static string QuoteShellWord(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\' or '='))
            return value;
        if (!value.Contains('"')) return "\"" + value.Replace("\\", "\\\\") + "\"";
        if (!value.Contains('\'')) return "'" + value + "'";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
