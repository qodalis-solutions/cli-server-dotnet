using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Services;

/// <summary>
/// Default implementation of <see cref="ICliServerInfoService"/> that provides
/// server version, OS/shell capabilities, and command descriptor mapping.
/// </summary>
public class CliServerInfoService : ICliServerInfoService
{
    private readonly CliServerOptions? _options;
    private readonly IServiceProvider? _services;

    /// <summary>Creates the service without server options (shell reported as enabled, no <c>shellAuth</c>).</summary>
    public CliServerInfoService()
    {
    }

    /// <summary>Creates the service reporting the configured shell state and access mode.</summary>
    [ActivatorUtilitiesConstructor]
    public CliServerInfoService(CliServerOptions options, IServiceProvider services)
    {
        _options = options;
        _services = services;
    }

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

        if (_options == null)
        {
            return new CliServerCapabilities { Shell = true, Os = os, ShellPath = shellPath, Version = ServerVersion };
        }

        var enabled = _options.EnableShell;
        if (!string.IsNullOrWhiteSpace(_options.Shell.Shell)) shellPath = _options.Shell.Shell;
        return new CliServerCapabilities
        {
            Shell = enabled,
            Os = os,
            ShellPath = shellPath,
            Version = ServerVersion,
            ShellAuth = enabled
                ? CliShellAccess.ResolveMode(_options.Shell, _services?.GetService<ICliAuthBridge>() != null)
                : null,
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

/// <summary>Response of <c>GET /api/v1/qcli/capabilities</c>.</summary>
public sealed class CliServerCapabilities
{
    /// <summary>The shell WebSocket is enabled.</summary>
    [JsonPropertyName("shell")]
    public bool Shell { get; init; }

    [JsonPropertyName("os")]
    public string Os { get; init; } = string.Empty;

    [JsonPropertyName("shellPath")]
    public string ShellPath { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("streaming")]
    public bool Streaming { get; init; } = true;

    /// <summary>
    /// Shell access mode when the shell is enabled: <c>users</c>, <c>token</c>, <c>custom</c>,
    /// <c>anonymous</c> or <c>locked</c> (see <see cref="CliShellAuthModes"/>).
    /// </summary>
    [JsonPropertyName("shellAuth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShellAuth { get; init; }
}
