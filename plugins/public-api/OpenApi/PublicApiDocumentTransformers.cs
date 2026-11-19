using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Qodalis.Cli.Plugin.Users.Authorization;

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>
/// The plugin's additions to the public document. Registered with <c>PostConfigure</c>, so they run after the host's
/// own transformers, and they only fill what is missing: the host's values always win.
/// </summary>
internal static class PublicApiDocumentTransformers
{
    public const string SchemeName = "qcliToken";
    public const string RequiredPermission = "x-required-permission";
    public const string HostTokenOnly = "x-host-token-only";

    /// <summary><c>x-required-permission</c> and <c>x-host-token-only</c> from the guard's metadata (C3).</summary>
    public static Task AddGuardExtensions(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken ct)
    {
        var guard = context.Description.ActionDescriptor.EndpointMetadata.OfType<IQcliAuthorizeMetadata>().LastOrDefault();
        if (guard == null) return Task.CompletedTask;

        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        if (guard.Permission != null && !operation.Extensions.ContainsKey(RequiredPermission))
            operation.Extensions[RequiredPermission] = new JsonNodeExtension(JsonValue.Create(guard.Permission));
        if (guard.HostTokenOnly && !operation.Extensions.ContainsKey(HostTokenOnly))
            operation.Extensions[HostTokenOnly] = new JsonNodeExtension(JsonValue.Create(true));
        return Task.CompletedTask;
    }

    /// <summary>The bearer scheme <c>qcliToken</c> and the top-level requirement on it, unless the host set them.</summary>
    public static Task AddSecurity(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        if (!document.Components.SecuritySchemes.ContainsKey(SchemeName))
        {
            document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "qcli_pat",
                Description = "API token (qcli_pat_…), sent as Authorization: Bearer <token>",
            };
        }

        if (document.Security is not { Count: > 0 })
        {
            document.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SchemeName, document)] = [] },
            ];
        }
        return Task.CompletedTask;
    }
}
