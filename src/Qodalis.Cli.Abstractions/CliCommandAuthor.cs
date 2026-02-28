namespace Qodalis.Cli.Abstractions;

public class CliCommandAuthor : ICliCommandAuthor
{
    public required string Name { get; set; }
    public required string Email { get; set; }
}

public static class DefaultLibraryAuthor
{
    public static readonly ICliCommandAuthor Instance = new CliCommandAuthor
    {
        Name = "Nicolae Lupei",
        Email = "nicolae.lupei@qodalis.com",
    };
}
