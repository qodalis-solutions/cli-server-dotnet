using Qodalis.Cli.Abstractions.Events;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// The users plugin's server events: <c>user:*</c> (<c>users:read</c>), <c>group:*</c> (<c>groups:read</c>),
/// <c>token:*</c> (<c>tokens:read</c> or the token's owner) and <c>session:*</c> (<c>sessions:read</c>). Published by
/// <see cref="AuthService"/> and <see cref="UsersService"/>, the layer every API and command goes through.
/// </summary>
public sealed class UsersEvents
{
    public const string UserCreated = "user:created";
    public const string UserUpdated = "user:updated";
    public const string UserDeleted = "user:deleted";
    public const string GroupCreated = "group:created";
    public const string GroupUpdated = "group:updated";
    public const string GroupDeleted = "group:deleted";
    public const string TokenCreated = "token:created";
    public const string TokenRevoked = "token:revoked";
    public const string SessionOpened = "session:opened";
    public const string SessionClosed = "session:closed";

    private static readonly CliEventAudience UsersAudience = CliEventAudience.WithPermission(UsersPermissions.UsersRead);
    private static readonly CliEventAudience GroupsAudience = CliEventAudience.WithPermission(UsersPermissions.GroupsRead);
    private static readonly CliEventAudience SessionsAudience = CliEventAudience.WithPermission(UsersPermissions.SessionsRead);

    private readonly ICliEventBus? _bus;

    public UsersEvents(ICliEventBus? bus)
    {
        _bus = bus;
    }

    /// <summary><c>user:created</c>, <c>user:updated</c> (profile, password, enabled, groups) or <c>user:deleted</c>.</summary>
    public Task UserAsync(string type, User user, string? actor) =>
        PublishAsync(type, new() { ["userId"] = user.Id, ["username"] = user.Name }, UsersAudience, actor);

    /// <summary><c>group:created</c>, <c>group:updated</c> or <c>group:deleted</c>.</summary>
    public Task GroupAsync(string type, string group, string? actor) =>
        PublishAsync(type, new() { ["group"] = group }, GroupsAudience, actor);

    /// <summary><c>token:created</c> or <c>token:revoked</c>, to <c>tokens:read</c> and the token's owner.</summary>
    public Task TokenAsync(string type, ApiToken token, string? actor) =>
        PublishAsync(type, new() { ["tokenId"] = token.Id, ["name"] = token.Name, ["userId"] = token.UserId },
            new CliEventAudience(UsersPermissions.TokensRead, token.UserId), actor);

    /// <summary><c>session:opened</c> or <c>session:closed</c> (sign-out, expiry, revocation).</summary>
    public Task SessionAsync(string type, Session session, string? actor = null) =>
        PublishAsync(type, new() { ["userId"] = session.UserId, ["username"] = session.UserName, ["sessionId"] = session.Id },
            SessionsAudience, actor);

    private Task PublishAsync(string type, Dictionary<string, object?> data, CliEventAudience audience, string? actor)
    {
        if (_bus == null) return Task.CompletedTask;
        if (actor != null) data["actor"] = actor;
        return _bus.PublishAsync(type, data, audience);
    }
}
