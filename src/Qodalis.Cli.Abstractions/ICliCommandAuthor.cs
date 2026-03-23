namespace Qodalis.Cli.Abstractions;

/// <summary>
/// Represents the author of a CLI command processor or module.
/// </summary>
public interface ICliCommandAuthor
{
    /// <summary>
    /// Gets the author's display name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the author's email address.
    /// </summary>
    string Email { get; }
}