namespace Qodalis.Cli.Abstractions;

/// <summary>
/// Default implementation of <see cref="ICliCommandAuthor"/> representing a command author.
/// </summary>
public class CliCommandAuthor : ICliCommandAuthor
{
    /// <summary>
    /// Gets or sets the author's display name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the author's email address.
    /// </summary>
    public required string Email { get; set; }
}

/// <summary>
/// Provides the default library author instance used when no custom author is specified.
/// </summary>
public static class DefaultLibraryAuthor
{
    /// <summary>
    /// The default author for built-in CLI command processors.
    /// </summary>
    public static readonly ICliCommandAuthor Instance = new CliCommandAuthor
    {
        Name = "Nicolae Lupei",
        Email = "nicolae.lupei@qodalis.com",
    };
}
