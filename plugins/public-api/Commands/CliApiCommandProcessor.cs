using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Plugin.Users.Stores;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>
/// <c>api</c> (= <c>api usage</c>), <c>api usage [--user &lt;name|id&gt;] [--days N] [--json]</c>: the public API usage
/// of an account (<see cref="IPublicApiUsage"/>). Your own with commands:execute; another account's with
/// <c>tokens:read</c>, like <c>GET /tokens?all=true</c>.
/// </summary>
public sealed class CliApiCommandProcessor : CliCommandProcessor
{
    /// <summary>The users plugin's permission to see every user's tokens: needed for another account's usage.</summary>
    public const string TokensReadPermission = UsersPermissions.TokensRead;

    /// <summary>The days of usage kept per token (the most <c>--days</c> takes).</summary>
    public const int UsageDays = 30;

    private readonly UsageProcessor _usage;

    public CliApiCommandProcessor(IServiceProvider services)
    {
        _usage = new UsageProcessor(services);
        Processors = [_usage];
    }

    public override string Command { get; set; } = "api";
    public override string Description { get; set; } = "Public API: requests per token and day, and the quota";
    public override string? Module { get; set; } = "public-api";
    public override string? Icon { get; set; } = "🔌";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        _usage.HandleAsync(command, cancellationToken);

    /// <summary><c>1000 per day, 60 per minute</c>; <c>unlimited</c>; <c>no API access</c> for a 0 limit.</summary>
    public static string FormatQuota(PublicApiQuota quota)
    {
        if ((quota.RequestsPerDay ?? 1) <= 0 || (quota.RequestsPerMinute ?? 1) <= 0) return "no API access";
        var parts = new[]
        {
            quota.RequestsPerDay is { } day ? $"{day} per day" : null,
            quota.RequestsPerMinute is { } minute ? $"{minute} per minute" : null,
        }.OfType<string>().ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : "unlimited";
    }

    /// <summary>The account's usage as text: today, the quota, then requests per day (rows) and token (columns) for <paramref name="days"/> days.</summary>
    public static string FormatAccountUsage(string name, PublicApiAccountUsage usage, int days)
    {
        var lines = new List<string>
        {
            $"{name}: {usage.Today} request(s) today (every token, revoked ones too)",
            $"Quota: {FormatQuota(usage.Quota)}",
        };
        if (usage.Tokens.Count == 0)
        {
            lines.Add($"{name} has no API tokens.");
            return string.Join('\n', lines);
        }
        var dates = usage.Tokens[0].Usage.Days.TakeLast(days).Select(d => d.Date).ToList();
        var headers = new[] { "Day" }.Concat(usage.Tokens.Select(t => t.Name)).Append("Total").ToArray();
        var rows = dates.Select(date =>
        {
            var counts = usage.Tokens.Select(t => t.Usage.Days.FirstOrDefault(d => d.Date == date)?.Requests ?? 0).ToList();
            return new[] { date }.Concat(counts.Select(Count)).Append(Count(counts.Sum())).ToArray();
        }).ToList();
        var totals = usage.Tokens.Select(t => t.Usage.Days.TakeLast(days).Sum(d => d.Requests)).ToList();
        rows.Add(new[] { "Total" }.Concat(totals.Select(Count)).Append(Count(totals.Sum())).ToArray());
        var widths = headers.Select((h, i) => rows.Select(r => r[i].Length).Append(h.Length).Max()).ToArray();
        // the day left-aligned, the counts right-aligned
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => i == 0 ? c.PadRight(widths[i]) : c.PadLeft(widths[i])));
        lines.Add(Line(headers));
        lines.AddRange(rows.Select(Line));
        return string.Join('\n', lines);
    }

    private static string Count(long n) => n.ToString(CultureInfo.InvariantCulture);

    private sealed class UsageProcessor(IServiceProvider services) : CliCommandProcessor
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public override string Command { get; set; } = "usage";
        public override string Description { get; set; } =
            "Shows the public API requests of an account per token and day, with its quota (your own unless --user)";
        public override string? Module { get; set; } = "public-api";

        public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
        [
            new CliCommandParameterDescriptor { Name = "user", Description = $"Another account, by name or id (needs {TokensReadPermission})", Type = CommandParameterType.String },
            new CliCommandParameterDescriptor
            {
                Name = "days",
                Description = $"How many days back, today included (1-{UsageDays}, default 7)",
                Type = CommandParameterType.Number,
                DefaultValue = 7,
            },
            new CliCommandParameterDescriptor { Name = "json", Description = "Print the usage as JSON", Type = CommandParameterType.Boolean },
        ];

        public override IEnumerable<CliCommandExample>? Examples { get; set; } =
        [
            new("api usage", "Your requests of the last 7 days"),
            new("api usage --user alice --days 30", "alice's requests of the last 30 days"),
        ];

        public override async Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var days = 7;
            if (command.Args.TryGetValue("days", out var rawDays) && rawDays != null
                && !(int.TryParse(rawDays.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out days) && days >= 1 && days <= UsageDays))
                return $"Error: --days must be a whole number from 1 to {UsageDays}";
            var caller = services.GetService<IHttpContextAccessor>()?.HttpContext?.GetCliPrincipal();
            string userId, userName;
            if (!command.Args.TryGetValue("user", out var rawUser) || rawUser == null)
            {
                if (caller == null) return "Error: no signed-in user: name the account with --user <name|id>";
                (userId, userName) = (caller.UserId, caller.UserName);
            }
            else
            {
                var key = rawUser.ToString()?.Trim();
                if (string.IsNullOrEmpty(key) || key is "True" or "true") return "Error: --user needs a name or id";
                await using var scope = services.CreateAsyncScope();
                var user = await scope.ServiceProvider.GetRequiredService<IUserStore>().GetUserAsync(key);
                if (user == null) return $"Error: user not found: {key}";
                if (caller != null && user.Id != caller.UserId && !caller.Can(TokensReadPermission))
                    return $"Error: another account's usage needs {TokensReadPermission}";
                (userId, userName) = (user.Id, user.Name);
            }

            PublicApiAccountUsage account;
            try
            {
                account = await services.GetRequiredService<IPublicApiUsage>().GetAccountUsageAsync(userId, cancellationToken);
            }
            catch (UsersApiException ex)
            {
                return $"Error: {ex.Message}";
            }
            if (command.Args.TryGetValue("json", out var json) && json?.ToString() is "True" or "true" or "")
            {
                return JsonSerializer.Serialize(new
                {
                    user = new { id = userId, name = userName },
                    days,
                    today = account.Today,
                    quota = account.Quota,
                    tokens = account.Tokens.Select(t => new
                    {
                        tokenId = t.TokenId,
                        name = t.Name,
                        usage = new { today = t.Usage.Today, last30Days = t.Usage.Last30Days, days = t.Usage.Days.TakeLast(days) },
                    }),
                }, Json);
            }
            return FormatAccountUsage(userName, account, days);
        }
    }
}
