using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.DataExplorer;

namespace Qodalis.Cli.Services;

public class DataExplorerExecutorService
{
    private readonly DataExplorerRegistry _registry;
    private readonly ILogger<DataExplorerExecutorService> _logger;

    public DataExplorerExecutorService(DataExplorerRegistry registry, ILogger<DataExplorerExecutorService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task<DataExplorerResult> ExecuteAsync(
        DataExplorerExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        var entry = _registry.Get(request.Source);

        if (entry is null)
        {
            return new DataExplorerResult
            {
                Success = false,
                Source = request.Source,
                Error = $"Unknown data source: {request.Source}"
            };
        }

        var (provider, options) = entry.Value;

        var context = new DataExplorerExecutionContext
        {
            Query = request.Query,
            Parameters = request.Parameters ?? [],
            Options = options
        };

        var sw = Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.Timeout);

            _logger.LogInformation("Executing data explorer query on source '{Source}': {Query}", request.Source, request.Query);

            var result = await provider.ExecuteAsync(context, timeoutCts.Token);

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

            result.RowCount = result.Rows?.Count ?? 0;

            _logger.LogInformation(
                "Data explorer query completed on source '{Source}' in {ElapsedMs}ms, {RowCount} rows",
                request.Source, result.ExecutionTime, result.RowCount);

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogWarning("Data explorer query timed out on source '{Source}' after {Timeout}ms", request.Source, options.Timeout);
            return new DataExplorerResult
            {
                Success = false,
                Source = request.Source,
                Language = options.Language,
                DefaultOutputFormat = options.DefaultOutputFormat,
                ExecutionTime = sw.ElapsedMilliseconds,
                Error = $"Query timed out after {options.Timeout}ms"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Data explorer query failed on source '{Source}'", request.Source);
            return new DataExplorerResult
            {
                Success = false,
                Source = request.Source,
                Language = options.Language,
                DefaultOutputFormat = options.DefaultOutputFormat,
                ExecutionTime = sw.ElapsedMilliseconds,
                Error = ex.Message
            };
        }
    }
}
