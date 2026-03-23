namespace Qodalis.Cli.Abstractions.Extensions;

/// <summary>
/// Extension methods for <see cref="string"/>.
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// Converts the first character of the string to lowercase (camelCase convention).
    /// </summary>
    /// <param name="str">The string to convert.</param>
    /// <returns>The string with its first character lowercased.</returns>
    public static string ToCamelCase(this string str)
    {
        if (string.IsNullOrEmpty(str) || str.Length < 2)
        {
            return str.ToLower();
        }

        return char.ToLowerInvariant(str[0]) + str[1..];
    }
}
