using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Services;

public interface ICliCommandExecutorService
{
    Task<CliServerResponse> ExecuteAsync(CliProcessCommand command, CancellationToken cancellationToken = default);
}
