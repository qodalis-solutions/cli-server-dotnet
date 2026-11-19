namespace Qodalis.Cli.Portal;

/// <summary>
/// The request path base check of the plugins that put it into URLs (docs, portal, public-api): a path base that
/// starts with <c>//</c> or <c>/\</c>, or holds whitespace or a control character (browsers drop tabs and line breaks,
/// so <c>/\t/evil.example</c> becomes <c>//evil.example</c>), would point those URLs at another host. Such requests are
/// answered 400 <see cref="InvalidBody"/>.
/// </summary>
public static class CliPathBase
{
    /// <summary>The answer's body: <c>{"error":"invalid path base"}</c>.</summary>
    public const string InvalidBody = "{\"error\":\"invalid path base\"}";

    /// <summary>True when <paramref name="pathBase"/> must not be put into a URL.</summary>
    public static bool IsUnsafe(string? pathBase)
    {
        if (string.IsNullOrEmpty(pathBase)) return false;
        if (pathBase.Length >= 2 && pathBase[0] == '/' && pathBase[1] is '/' or '\\') return true;
        foreach (var c in pathBase)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return true;
        }
        return false;
    }
}
