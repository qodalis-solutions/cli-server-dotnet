namespace Qodalis.Cli.Abstractions.Extensions;

public static class StringExtensions
{
    public static string ToCamelCase(this string str)
    {
        if (string.IsNullOrEmpty(str) || str.Length < 2)
        {
            return str.ToLower();
        }

        return char.ToLowerInvariant(str[0]) + str[1..];
    }
}
