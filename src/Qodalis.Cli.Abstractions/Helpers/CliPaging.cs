namespace Qodalis.Cli.Abstractions.Helpers;

/// <summary>
/// The rows a list command was asked for, read from <c>--skip</c> and <c>--take</c>.
/// Commands that can return many rows declare <see cref="CliPaging.Parameters"/>, read the
/// request with <see cref="CliPaging.From"/>, query with <see cref="Skip"/> and <see cref="Take"/>,
/// and write the rows with <c>WritePagedTable</c> so the terminal shows which rows these are
/// and how to get the next ones.
/// </summary>
/// <param name="Skip">Rows to skip.</param>
/// <param name="Take">Rows to return at most.</param>
public sealed record CliPageRequest(int Skip, int Take);

/// <summary>
/// The <c>--skip</c> / <c>--take</c> convention shared by list commands.
/// </summary>
public static class CliPaging
{
    /// <summary>Rows returned when the command line does not say.</summary>
    public const int DefaultTake = 50;

    /// <summary>The largest <c>--take</c> accepted; larger values are lowered to it.</summary>
    public const int MaxTake = 500;

    /// <summary>
    /// The <c>--skip</c> and <c>--take</c> parameters, to add to a command's
    /// <see cref="ICliCommandProcessor.Parameters"/> so help and completion show them.
    /// </summary>
    public static IReadOnlyList<ICliCommandParameterDescriptor> Parameters(int defaultTake = DefaultTake) =>
    [
        new CliCommandParameterDescriptor
        {
            Name = "skip",
            Description = "Rows to skip",
            Type = CommandParameterType.Number,
            DefaultValue = 0,
        },
        new CliCommandParameterDescriptor
        {
            Name = "take",
            Description = $"Rows to show (at most {MaxTake})",
            Type = CommandParameterType.Number,
            DefaultValue = defaultTake,
        },
    ];

    /// <summary>
    /// The rows asked for by <paramref name="command"/>: <c>--skip</c> (default 0; negative values
    /// count as 0) and <c>--take</c> (default <paramref name="defaultTake"/>, at most
    /// <see cref="MaxTake"/>; zero, negative or invalid values fall back to the default).
    /// </summary>
    public static CliPageRequest From(CliProcessCommand command, int defaultTake = DefaultTake)
    {
        ArgumentNullException.ThrowIfNull(command);
        var skip = Math.Max(ReadInt(command, "skip") ?? 0, 0);
        var take = ReadInt(command, "take") is int t && t > 0 ? Math.Min(t, MaxTake) : defaultTake;
        return new CliPageRequest(skip, take);
    }

    private static int? ReadInt(CliProcessCommand command, string name) =>
        command.Args.TryGetValue(name, out var value) && int.TryParse(value?.ToString(), out var number)
            ? number
            : null;
}
