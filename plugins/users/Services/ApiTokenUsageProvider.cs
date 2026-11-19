using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Supplies the <c>usage</c> of token views (<c>GET /tokens</c>, <see cref="IApiTokenManager.ListAsync"/>). Optional:
/// without a registered provider the views carry no <c>usage</c>. The public-api plugin registers one.
/// </summary>
public interface IApiTokenUsageProvider
{
    /// <summary>The usage of each token in <paramref name="tokenIds"/>; a token missing from the result gets no <c>usage</c>.</summary>
    Task<IReadOnlyDictionary<string, ApiTokenUsage>> GetUsageAsync(IReadOnlyCollection<string> tokenIds,
        CancellationToken ct = default);
}

internal static class ApiTokenUsageViews
{
    /// <summary>
    /// Sets <see cref="ApiTokenView.Usage"/> on <paramref name="views"/> with one provider call. A failing provider is
    /// logged and the views are left without usage (a listing never fails because of it).
    /// </summary>
    public static async Task AddUsageAsync(IApiTokenUsageProvider? provider, IReadOnlyList<ApiTokenView> views,
        ILogger logger, CancellationToken ct = default)
    {
        if (provider == null || views.Count == 0) return;
        try
        {
            var usage = await provider.GetUsageAsync(views.Select(v => v.Id).ToList(), ct);
            foreach (var view in views)
                view.Usage = usage.TryGetValue(view.Id, out var u) ? u : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The token usage provider failed; tokens are listed without usage");
            foreach (var view in views) view.Usage = null;
        }
    }
}
