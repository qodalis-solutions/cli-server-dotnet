using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Sliding-window per-IP limiter for credential endpoints (login, verify-password,
/// self password change): <see cref="UsersModuleOptions.MaxAttemptsPerIpPerMinute"/> per minute.
/// </summary>
public sealed class IpRateLimiter
{
    private const long WindowMs = 60_000;
    private const int MaxTrackedIps = 50_000;

    private readonly ConcurrentDictionary<string, Queue<long>> _attempts = new();
    private readonly UsersModuleOptions _options;
    private readonly TimeProvider _time;

    public IpRateLimiter(UsersModuleOptions options, TimeProvider? timeProvider = null)
    {
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Client IP. When <see cref="UsersModuleOptions.TrustProxy"/> is on, the <c>X-Forwarded-For</c> entry
    /// at index <c>count - TrustedProxyHops</c> (the one appended by the outermost trusted proxy; the
    /// leftmost entries are client-controlled), or the leftmost entry when there are fewer entries.
    /// Otherwise, or when the header is absent or empty, the connection's remote address.
    /// </summary>
    public string ClientIp(HttpContext context)
    {
        if (_options.TrustProxy)
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                var entries = forwarded.Split(',').Select(e => e.Trim()).ToArray();
                var hops = Math.Max(1, _options.TrustedProxyHops);
                var ip = entries[Math.Max(0, entries.Length - hops)];
                if (!string.IsNullOrEmpty(ip)) return ip;
            }
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>Records an attempt; throws 429 when the IP exceeded the limit within the last minute.</summary>
    public void Acquire(HttpContext context)
    {
        var limit = _options.MaxAttemptsPerIpPerMinute;
        if (limit <= 0) return;

        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (_attempts.Count > MaxTrackedIps) Prune(now);

        var queue = _attempts.GetOrAdd(ClientIp(context), _ => new Queue<long>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() <= now - WindowMs) queue.Dequeue();
            if (queue.Count >= limit)
                throw new UsersApiException(429, "too many requests, try again later");
            queue.Enqueue(now);
        }
    }

    private void Prune(long now)
    {
        foreach (var (ip, queue) in _attempts)
        {
            lock (queue)
            {
                while (queue.Count > 0 && queue.Peek() <= now - WindowMs) queue.Dequeue();
                if (queue.Count == 0) _attempts.TryRemove(ip, out _);
            }
        }
    }
}
