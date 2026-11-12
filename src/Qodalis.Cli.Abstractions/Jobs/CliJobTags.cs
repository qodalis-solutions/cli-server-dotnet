namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// Rules for job tags (<see cref="CliJobOptions.Tags"/>), the same on every server: each tag 1-64 characters without
/// commas, at most 20 per job, trimmed, duplicates (ignoring case) dropped keeping the first spelling. Filtering keeps
/// the jobs that have every tag asked for, ignoring case.
/// </summary>
public static class CliJobTags
{
    /// <summary>Most tags a job may have.</summary>
    public const int MaxTags = 20;

    /// <summary>Longest tag.</summary>
    public const int MaxTagLength = 64;

    /// <summary>
    /// Validates and normalizes tags: trimmed, duplicates (ignoring case) dropped keeping the first spelling;
    /// <c>null</c> is no tags.
    /// </summary>
    /// <exception cref="ArgumentException">A tag is null, empty, longer than 64 characters or has a comma, or there are
    /// more than 20 (<see cref="ArgumentException.ParamName"/> is <c>Tags</c>).</exception>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? tags)
    {
        if (tags == null) return [];
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            var tag = raw?.Trim();
            if (string.IsNullOrEmpty(tag) || tag.Length > MaxTagLength || tag.Contains(','))
                throw new ArgumentException($"Invalid tag '{raw}': 1-{MaxTagLength} characters, no commas", nameof(CliJobOptions.Tags));
            if (seen.Add(tag)) result.Add(tag);
        }
        if (result.Count > MaxTags)
            throw new ArgumentException($"A job has at most {MaxTags} tags", nameof(CliJobOptions.Tags));
        return result;
    }

    /// <summary>Whether <paramref name="jobTags"/> holds every tag in <paramref name="tags"/> (ignoring case).</summary>
    public static bool HasAll(IEnumerable<string>? jobTags, IReadOnlyCollection<string> tags)
    {
        if (tags.Count == 0) return true;
        var own = new HashSet<string>(jobTags ?? [], StringComparer.OrdinalIgnoreCase);
        return tags.All(own.Contains);
    }

    /// <summary>A <c>tag</c> query parameter (repeated or comma-separated) as trimmed tags.</summary>
    public static IReadOnlyList<string> FromQuery(IEnumerable<string?>? values) =>
        values == null
            ? []
            : values.Where(v => v != null)
                .SelectMany(v => v!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
}
