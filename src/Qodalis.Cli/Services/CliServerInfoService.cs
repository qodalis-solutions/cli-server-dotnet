using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Services;

/// <summary>
/// Default implementation of <see cref="ICliServerInfoService"/> that provides
/// server version, OS/shell capabilities, and command descriptor mapping.
/// </summary>
public class CliServerInfoService : ICliServerInfoService
{
    /// <inheritdoc />
    public string ServerVersion => "1.0.0";

    /// <inheritdoc />
    public object GetCapabilities()
    {
        var os = Environment.OSVersion.Platform switch
        {
            PlatformID.Win32NT => "win32",
            PlatformID.Unix => OperatingSystem.IsMacOS() ? "darwin" : "linux",
            _ => "unknown",
        };

        var shell = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? "powershell"
            : "bash";

        var shellPath = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
            : "/bin/bash";

        return new
        {
            Shell = true,
            Os = os,
            ShellPath = shellPath,
            Version = ServerVersion,
            Streaming = true,
        };
    }

    /// <inheritdoc />
    public CliServerCommandDescriptor MapToDescriptor(ICliCommandProcessor processor)
    {
        return new CliServerCommandDescriptor
        {
            Command = processor.Command,
            Description = processor.Description,
            Version = processor.Version,
            ApiVersion = processor.ApiVersion,
            Parameters = processor.Parameters?.Select(p => new CliCommandParameterDescriptorDto
            {
                Name = p.Name,
                Aliases = p.Aliases,
                Description = p.Description,
                Required = p.Required,
                Type = p.Type,
                DefaultValue = p.DefaultValue,
            }).ToList(),
            Processors = processor.Processors?.Select(MapToDescriptor).ToList(),
        };
    }
}
