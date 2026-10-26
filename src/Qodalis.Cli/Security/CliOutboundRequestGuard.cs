using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Qodalis.Cli.Security;

/// <summary>
/// Thrown when an outbound request is refused because its host resolves to a private or local address.
/// </summary>
public sealed class CliPrivateNetworkBlockedException : HttpRequestException
{
    /// <summary>Creates the exception for <paramref name="host"/> resolving to <paramref name="address"/>.</summary>
    public CliPrivateNetworkBlockedException(string host, IPAddress address)
        : base($"Blocked: {host} resolves to a private or local address ({address}). Set AllowPrivateNetworks to allow it.")
    {
        Host = host;
        Address = address;
    }

    /// <summary>The host name (or IP literal) that was requested.</summary>
    public string Host { get; }

    /// <summary>The private or local address the host resolved to.</summary>
    public IPAddress Address { get; }
}

/// <summary>
/// Server-side request forgery (SSRF) guard for commands that make outbound HTTP requests on behalf of a
/// user (such as <c>http get</c>). <see cref="CreateHandler"/> returns a handler whose
/// <see cref="SocketsHttpHandler.ConnectCallback"/> resolves the host, refuses the connection when
/// <em>any</em> resolved address is loopback, private, link-local, CGNAT or unspecified, and connects to
/// the vetted address itself. Because the check runs on the connection, it also covers every redirect hop
/// and cannot be bypassed by DNS rebinding between the check and the connect.
/// </summary>
public static class CliOutboundRequestGuard
{
    /// <summary>
    /// True when <paramref name="address"/> is loopback (127.0.0.0/8, ::1), private (10/8, 172.16/12,
    /// 192.168/16, fc00::/7), link-local (169.254/16, fe80::/10), CGNAT (100.64/10) or unspecified
    /// (0.0.0.0/8, ::). IPv4-mapped IPv6 addresses (<c>::ffff:a.b.c.d</c>) are checked as IPv4.
    /// </summary>
    public static bool IsPrivateOrLocal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0                                   // 0.0.0.0/8 (unspecified / "this network")
                || b[0] == 127                                 // 127.0.0.0/8 loopback
                || b[0] == 10                                  // 10.0.0.0/8
                || (b[0] == 172 && (b[1] & 0xF0) == 16)        // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                // 169.254.0.0/16 link-local (cloud metadata)
                || (b[0] == 100 && (b[1] & 0xC0) == 64);       // 100.64.0.0/10 CGNAT
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any))
                return true;
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC                       // fc00::/7 unique local
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);    // fe80::/10 link-local
        }

        return true; // unknown address family: refuse
    }

    /// <summary>
    /// True when <paramref name="uri"/> is an absolute <c>http</c> or <c>https</c> URL.
    /// </summary>
    public static bool IsAllowedScheme(Uri uri)
        => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Creates an HTTP handler for user-requested outbound calls. With
    /// <paramref name="allowPrivateNetworks"/> <c>false</c> every connection (including redirect hops) is
    /// refused with <see cref="CliPrivateNetworkBlockedException"/> when the host resolves to a private or
    /// local address; the system proxy is not used, so the check applies to the real destination.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks)
    {
        var handler = new SocketsHttpHandler();
        if (allowPrivateNetworks) return handler;

        handler.UseProxy = false;
        handler.ConnectCallback = ConnectToPublicAddressAsync;
        return handler;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and returns its addresses, throwing
    /// <see cref="CliPrivateNetworkBlockedException"/> when any of them is private or local.
    /// </summary>
    public static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken cancellationToken = default)
    {
        var trimmed = host.Trim('[', ']');
        var addresses = IPAddress.TryParse(trimmed, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(trimmed, cancellationToken);

        if (addresses.Length == 0)
            throw new HttpRequestException($"Could not resolve host: {host}");

        foreach (var address in addresses)
        {
            if (IsPrivateOrLocal(address))
                throw new CliPrivateNetworkBlockedException(host, address);
        }
        return addresses;
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await ResolvePublicAddressesAsync(endpoint.Host, cancellationToken);

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (ex is OperationCanceledException) throw;
                last = ex;
            }
        }
        throw last ?? new HttpRequestException($"Could not connect to {endpoint.Host}");
    }
}
