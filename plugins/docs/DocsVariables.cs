using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Docs;

/// <summary>
/// <c>{{ key }}</c> variables: the built-in <c>baseUrl</c> and <c>name</c> and the host's own, replaced in titles and
/// markdown per request. Unknown keys are left as written.
/// </summary>
internal static partial class DocsVariables
{
    public const string BaseUrl = "baseUrl";
    public const string Name = "name";

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_.-]*)\s*\}\}")]
    private static partial Regex VariableRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]*$")]
    private static partial Regex KeyRegex();

    /// <summary>Throws <see cref="ArgumentException"/> for a built-in or malformed key.</summary>
    public static void Validate(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (key is BaseUrl or Name) throw new ArgumentException($"docs variable is built in: {key}");
            if (!KeyRegex().IsMatch(key)) throw new ArgumentException($"invalid docs variable: {key}");
        }
    }

    public static string Replace(string text, IReadOnlyDictionary<string, string> values) =>
        VariableRegex().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
}
