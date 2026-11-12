using System.Net;
using System.Net.Sockets;

namespace MoPLauncher.Config;

// A parsed "host[:port]" server address, resolved to the IPv4 address the
// in-memory patches need.
public sealed record ServerEndpoint(string Host, int Port, IPAddress Address)
{
    public const int DefaultPort = 3724;

    // What goes into Config.wtf's realmlist.
    public string Realmlist => Port == DefaultPort ? Host : $"{Host}:{Port}";

    public static bool TryParse(string? text, out string host, out int port)
    {
        host = (text ?? string.Empty).Trim();
        port = DefaultPort;

        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(host[(colon + 1)..], out port) || port is < 1 or > 65535)
                return false;

            host = host[..colon].Trim();
        }

        return host.Length > 0 && Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;
    }

    public static async Task<ServerEndpoint> ResolveAsync(string? text)
    {
        if (!TryParse(text, out var host, out var port))
            throw new InvalidOperationException("The server address isn't valid. Use an IP or hostname, like 192.168.1.50.");

        if (IPAddress.TryParse(host, out var literal))
            return new ServerEndpoint(host, port, literal);

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
            addresses = [];
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException($"Couldn't find the server '{host}'. Check the address and your connection.");

        return new ServerEndpoint(host, port, addresses[0]);
    }
}
