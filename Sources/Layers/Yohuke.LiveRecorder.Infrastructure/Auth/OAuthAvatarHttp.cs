using System.Net;
using System.Net.Sockets;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

internal static class OAuthAvatarHttp
{
    private const int MAX_SOURCE_LENGTH = 4096;
    private static readonly IPNetwork[] NonPublicIPv4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/3"),
    ];
    private static readonly IPNetwork GlobalIPv6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] NonPublicIPv6 =
    [
        IPNetwork.Parse("2001::/23"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("3fff::/20"),
    ];

    internal static string ResolveRelativeSource(string source, Uri userInfoEndpoint)
    {
        if (source.Length is 0 or > MAX_SOURCE_LENGTH || source[0] is '?' or '#'
            || source.StartsWith("//", StringComparison.Ordinal) || !Uri.IsWellFormedUriString(source, UriKind.Relative))
        {
            return source;
        }

        var path = source[0] == '/' ? source : "/" + source;
        return Uri.TryCreate(userInfoEndpoint, path, out var uri) ? uri.AbsoluteUri : string.Empty;
    }

    internal static Uri? GetSource(string source) =>
        source.Length <= MAX_SOURCE_LENGTH && Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.HostNameType == UriHostNameType.Dns
        && !uri.IsLoopback && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            ? uri
            : null;

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext connection, CancellationToken cancellationToken)
    {
        var endpoint = connection.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        SocketException? lastError = null;
        foreach (var address in addresses.Where(IsPublicAddress))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                lastError = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("No public address was reachable for the OAuth avatar.", lastError);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !NonPublicIPv4.Any(network => network.Contains(address)),
            AddressFamily.InterNetworkV6 => GlobalIPv6.Contains(address) && !NonPublicIPv6.Any(network => network.Contains(address)),
            _ => false,
        };
    }
}
