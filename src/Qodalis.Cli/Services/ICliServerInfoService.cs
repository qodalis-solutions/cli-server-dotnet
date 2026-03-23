using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Services;

/// <summary>
/// Service providing server metadata, capabilities, and command descriptor mapping.
/// </summary>
public interface ICliServerInfoService
{
    /// <summary>Returns the current server version.</summary>
    string ServerVersion { get; }

    /// <summary>Returns server capabilities including OS, shell, and version.</summary>
    object GetCapabilities();

    /// <summary>Maps a command processor to a serializable descriptor.</summary>
    CliServerCommandDescriptor MapToDescriptor(ICliCommandProcessor processor);
}
