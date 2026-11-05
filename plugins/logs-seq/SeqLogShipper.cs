using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;

namespace Qodalis.Cli.Plugin.Logs.Seq;

/// <summary>
/// Sends log events to Seq's <c>/ingest/clef</c> in batches from a background loop. Logging never waits for Seq and
/// never throws: while Seq is slow or down, events wait in a bounded queue (the oldest are dropped beyond
/// <see cref="SeqLogsOptions.QueueLimit"/>) and the batch is retried with a growing delay. Problems go to standard
/// error, at most once a minute, never through <c>ILogger</c> (which would feed them back here).
/// </summary>
internal sealed class SeqLogShipper : IDisposable
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorReportInterval = TimeSpan.FromMinutes(1);

    private readonly SeqLogsOptions _options;
    private readonly HttpClient _http;
    private readonly TextWriter _errors;
    private readonly Channel<string> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private DateTimeOffset _lastErrorReport = DateTimeOffset.MinValue;
    private int _disposed;

    public SeqLogShipper(SeqLogsOptions options, HttpMessageHandler? handler = null, TextWriter? errors = null)
    {
        _options = options;
        _errors = errors ?? Console.Error;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = options.BaseAddress;
        _http.Timeout = options.RequestTimeout;
        if (!string.IsNullOrEmpty(options.ApiKey)) _http.DefaultRequestHeaders.Add("X-Seq-ApiKey", options.ApiKey);
        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(options.QueueLimit)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Queues one CLEF event (a JSON object on one line).</summary>
    public void Enqueue(string clefEvent) => _queue.Writer.TryWrite(clefEvent);

    private async Task RunAsync()
    {
        var batch = new List<string>(_options.BatchSize);
        var failures = 0;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stopping.Token))
            {
                // Give a burst a moment to arrive, so it goes in one request.
                if (_options.FlushInterval > TimeSpan.Zero && _queue.Reader.Count < _options.BatchSize)
                    await Task.Delay(_options.FlushInterval, _stopping.Token);

                while (batch.Count < _options.BatchSize && _queue.Reader.TryRead(out var next)) batch.Add(next);
                while (!await TrySendAsync(batch, _stopping.Token))
                {
                    failures++;
                    var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failures), MaxRetryDelay.TotalSeconds));
                    await Task.Delay(delay, _stopping.Token);
                }
                failures = 0;
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Stopping: what is left is sent by Dispose.
        }

        // Send what is left, once, within the drain timeout.
        while (_queue.Reader.TryRead(out var rest)) batch.Add(rest);
        if (batch.Count == 0) return;
        using var drain = new CancellationTokenSource(DrainTimeout);
        for (var i = 0; i < batch.Count; i += _options.BatchSize)
        {
            if (!await TrySendAsync(batch.GetRange(i, Math.Min(_options.BatchSize, batch.Count - i)), drain.Token)) return;
        }
    }

    private async Task<bool> TrySendAsync(List<string> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return true;
        try
        {
            var body = new StringContent(string.Join('\n', batch), Encoding.UTF8);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.serilog.clef");
            using var response = await _http.PostAsync("ingest/clef", body, cancellationToken);
            if (response.IsSuccessStatusCode) return true;

            // Seq rejects a malformed batch with 400: retrying it would block every later event.
            var rejected = (int)response.StatusCode == 400;
            ReportError($"Seq answered {(int)response.StatusCode} {response.ReasonPhrase}" +
                (rejected ? $"; {batch.Count} events dropped" : "; retrying"));
            return rejected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ReportError($"{ex.Message}; retrying");
            return false;
        }
    }

    private void ReportError(string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastErrorReport < ErrorReportInterval) return;
        _lastErrorReport = now;
        try
        {
            _errors.WriteLine($"[qodalis-cli] Could not send logs to Seq at {_options.ServerUrl}: {message}");
        }
        catch (ObjectDisposedException)
        {
            // Standard error closed at shutdown.
        }
    }

    /// <summary>Stops the loop and sends the queued events, waiting at most a few seconds.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _queue.Writer.TryComplete();
        _stopping.Cancel();
        try
        {
            _loop.Wait(DrainTimeout + TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The loop handles its own errors; nothing more to send.
        }
        _http.Dispose();
        _stopping.Dispose();
    }
}
