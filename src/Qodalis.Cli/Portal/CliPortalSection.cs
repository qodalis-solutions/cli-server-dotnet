namespace Qodalis.Cli.Portal;

/// <summary>
/// A section of the portal site (<c>portal.json</c>), registered as a singleton by the plugin that serves it: the docs
/// plugin registers <see cref="Docs"/>, the public-api plugin <see cref="Api"/>. The portal plugin reads every
/// registered section; the plugins never reference each other.
/// </summary>
/// <param name="Kind"><see cref="Docs"/> or <see cref="Api"/>.</param>
/// <param name="Title">The section's label on the portal (e.g. <c>Guides</c>, <c>API reference</c>).</param>
/// <param name="BasePath">Where the plugin serves its routes (e.g. <c>/qcli/docs</c>, <c>/qcli/api</c>).</param>
public sealed record CliPortalSection(string Kind, string Title, string BasePath)
{
    /// <summary>The docs plugin's markdown guides.</summary>
    public const string Docs = "docs";

    /// <summary>The public-api plugin's OpenAPI document and Swagger UI.</summary>
    public const string Api = "api";

    /// <summary>The number of pages of a docs section (the docs plugin sets it), shown by the <c>portal</c> command; null when unknown.</summary>
    public Func<int>? PageCount { get; init; }

    /// <summary>Equal when kind, title and base path are (the page count is not compared).</summary>
    public bool Equals(CliPortalSection? other) =>
        other is not null && Kind == other.Kind && Title == other.Title && BasePath == other.BasePath;

    public override int GetHashCode() => HashCode.Combine(Kind, Title, BasePath);
}
