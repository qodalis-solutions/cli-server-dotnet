using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Services;

/// <summary>
/// Service for executing data explorer queries against registered providers: the source's permissions, rate limit,
/// read-only rules, allowed objects and masked columns first, then the provider with a timeout, then the row limit,
/// the masked-columns check of the result, column masking and the audit entry (event <c>data_query</c>).
/// </summary>
public class DataExplorerExecutorService : IDataExplorerExecutorService
{
    /// <summary>The audit event of a data explorer execute.</summary>
    public const string AuditEvent = "data_query";

    /// <summary>Longest query text kept in an audit entry.</summary>
    public const int AuditQueryMaxLength = 2000;

    private readonly IDataExplorerRegistry _registry;
    private readonly ILogger<DataExplorerExecutorService> _logger;
    private readonly IServiceProvider? _services;
    private readonly DataExplorerRateLimiter _rateLimiter;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of <see cref="DataExplorerExecutorService"/>.
    /// </summary>
    /// <param name="registry">The data explorer provider registry.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="services">Where the audit log (<see cref="IAuditStore"/>) is resolved, on every query.</param>
    /// <param name="rateLimiter">The rate limiter (a new one when <c>null</c>).</param>
    /// <param name="time">The clock of audit entries.</param>
    public DataExplorerExecutorService(
        IDataExplorerRegistry registry,
        ILogger<DataExplorerExecutorService> logger,
        IServiceProvider? services = null,
        DataExplorerRateLimiter? rateLimiter = null,
        TimeProvider? time = null)
    {
        _registry = registry;
        _logger = logger;
        _services = services;
        _rateLimiter = rateLimiter ?? new DataExplorerRateLimiter(time);
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecuteRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, null, cancellationToken);

    /// <inheritdoc />
    public async Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecuteRequest request,
        DataExplorerCaller? caller,
        CancellationToken cancellationToken = default)
    {
        var entry = _registry.Get(request.Source);

        if (entry is null)
        {
            return new DataExplorerResult
            {
                Success = false,
                Source = request.Source,
                Error = $"Unknown data source: {request.Source}",
                ErrorCode = DataExplorerErrorCodes.UnknownSource,
            };
        }

        var (provider, options) = entry.Value;

        var missing = DataExplorerPolicy.MissingPermissions(options.RequiredPermissions, caller);
        if (missing.Count > 0)
        {
            return await RefuseAsync(request, options, caller,
                $"forbidden: missing permission {string.Join(", ", missing)}", DataExplorerErrorCodes.Forbidden);
        }
        if (!_rateLimiter.TryAcquire(options, caller))
        {
            var limit = options.RateLimit!;
            return await RefuseAsync(request, options, caller,
                $"Too many queries: at most {limit.MaxQueries} per {limit.WindowSeconds} s", DataExplorerErrorCodes.RateLimited);
        }
        if (options.ReadOnly && !provider.ReadOnlyQueries &&
            DataExplorerPolicy.ReadOnlyError(options.Language, request.Query) is { } readOnlyError)
        {
            return await RefuseAsync(request, options, caller, readOnlyError, DataExplorerErrorCodes.ReadOnly);
        }
        if (DataExplorerPolicy.ObjectsError(options, request.Query) is { } objectsError)
        {
            return await RefuseAsync(request, options, caller, objectsError, DataExplorerErrorCodes.ObjectNotAllowed);
        }
        if (DataExplorerPolicy.MaskedColumnError(options, request.Query) is { } maskedError)
        {
            return await RefuseAsync(request, options, caller, maskedError, DataExplorerErrorCodes.MaskedColumn);
        }

        var context = new DataExplorerExecutionContext
        {
            Query = request.Query,
            Parameters = request.Parameters ?? [],
            Options = options
        };

        var sw = Stopwatch.StartNew();
        DataExplorerResult result;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.Timeout);

            _logger.LogInformation("Executing data explorer query on source '{Source}': {Query}", request.Source, request.Query);

            result = await provider.ExecuteAsync(context, timeoutCts.Token);

            sw.Stop();
            result.ExecutionTime = sw.ElapsedMilliseconds;
            result.Source = request.Source;
            result.Language = options.Language;
            result.DefaultOutputFormat = options.DefaultOutputFormat;

            // Enforce maxRows
            if (result.Rows != null && result.Rows.Count > options.MaxRows)
            {
                result.Rows = result.Rows.Take(options.MaxRows).ToList();
                result.Truncated = true;
            }

            if (result.Success &&
                DataExplorerPolicy.MaskedResultError(options, request.Query, result.Columns) is { } maskedResult)
            {
                // A masked column came back under another name: withhold the rows
                _logger.LogWarning("Data explorer result withheld on source '{Source}': {Error}", request.Source, maskedResult);
                result = Failure(request.Source, options, maskedResult, sw.ElapsedMilliseconds, DataExplorerErrorCodes.MaskedColumn);
            }

            if (result.Success)
            {
                DataExplorerPolicy.FilterListing(result, options, request.Query);
                DataExplorerPolicy.ApplyColumnPolicy(result, options);
            }

            result.RowCount = result.Rows?.Count ?? 0;

            _logger.LogInformation(
                "Data explorer query completed on source '{Source}' in {ElapsedMs}ms, {RowCount} rows",
                request.Source, result.ExecutionTime, result.RowCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogWarning("Data explorer query timed out on source '{Source}' after {Timeout}ms", request.Source, options.Timeout);
            result = Failure(request.Source, options, $"Query timed out after {options.Timeout}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Data explorer query failed on source '{Source}'", request.Source);
            result = Failure(request.Source, options, ex.Message, sw.ElapsedMilliseconds);
        }

        await AuditAsync(request, options, caller, result, sw.ElapsedMilliseconds);
        return result;
    }

    private async Task<DataExplorerResult> RefuseAsync(
        DataExplorerExecuteRequest request,
        DataExplorerProviderOptions options,
        DataExplorerCaller? caller,
        string error,
        string errorCode)
    {
        _logger.LogWarning("Data explorer query refused on source '{Source}' ({Code}): {Error}", request.Source, errorCode, error);
        var result = Failure(request.Source, options, error, 0, errorCode);
        await AuditAsync(request, options, caller, result, 0);
        return result;
    }

    private static DataExplorerResult Failure(
        string source,
        DataExplorerProviderOptions options,
        string error,
        long executionTime,
        string? errorCode = null) => new()
    {
        Success = false,
        Source = source,
        Language = options.Language,
        DefaultOutputFormat = options.DefaultOutputFormat,
        ExecutionTime = executionTime,
        Error = error,
        ErrorCode = errorCode,
    };

    /// <summary>
    /// Writes the <c>data_query</c> audit entry when the source audits and the server keeps an audit log
    /// (<see cref="IAuditStore"/>, e.g. the users plugin). A failing audit log does not fail the query.
    /// </summary>
    private async Task AuditAsync(
        DataExplorerExecuteRequest request,
        DataExplorerProviderOptions options,
        DataExplorerCaller? caller,
        DataExplorerResult result,
        long durationMs)
    {
        if (!options.Audit) return;
        try
        {
            var store = _services?.GetService<IAuditStore>();
            if (store == null) return;

            var query = request.Query.Length > AuditQueryMaxLength
                ? request.Query[..AuditQueryMaxLength] + "…"
                : request.Query;
            var properties = new Dictionary<string, object?>
            {
                ["source"] = options.Name,
                ["query"] = query,
                ["success"] = result.Success,
                ["rowCount"] = result.RowCount,
                ["durationMs"] = durationMs,
            };
            if (result.Error != null) properties["error"] = result.Error;

            await store.LogAsync(new AuditEntry
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds(),
                Event = AuditEvent,
                Username = caller?.UserName ?? "anonymous",
                UserId = caller?.UserId,
                Details = options.Name,
                Target = options.Name,
                Properties = properties,
                Metadata = new AuditMetadata
                {
                    RemoteAddress = caller?.RemoteAddress,
                    SessionId = caller?.SessionId,
                    DurationMs = durationMs,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the data explorer audit entry for source '{Source}'", options.Name);
        }
    }
}

/// <summary>Builds the <see cref="DataExplorerCaller"/> of a request from the server's authentication.</summary>
public static class DataExplorerCallers
{
    /// <summary>
    /// The caller of an HTTP request (or an MCP call): the signed-in user, the client address, and the permission
    /// check only when the server is protected (<see cref="CliServerOptions.RequireAuthentication"/>).
    /// </summary>
    /// <param name="http">The request, when there is one.</param>
    /// <param name="principal">The signed-in user (read from <paramref name="http"/> when <c>null</c>).</param>
    /// <param name="services">Where <see cref="CliServerOptions"/> is resolved (the request's services when <c>null</c>).</param>
    public static DataExplorerCaller From(HttpContext? http, CliPrincipal? principal = null, IServiceProvider? services = null)
    {
        principal ??= http?.GetCliPrincipal();
        services ??= http?.RequestServices;
        var protectedServer = services?.GetService<CliServerOptions>()?.RequireAuthentication == true;
        return new DataExplorerCaller
        {
            UserId = principal?.UserId,
            UserName = principal?.UserName,
            SessionId = principal?.SessionId,
            RemoteAddress = http?.Connection.RemoteIpAddress?.ToString(),
            HasPermission = protectedServer
                ? principal != null ? principal.Can : _ => false
                : null,
        };
    }
}
