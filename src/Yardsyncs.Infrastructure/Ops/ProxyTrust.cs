using System.Net;

namespace Yardsyncs.Infrastructure.Ops;

/// <summary>
/// Which proxies may tell the app the client's address and scheme (X-Forwarded-For / -Proto / -Host). Only requests
/// that come from these networks are believed; anyone else sending the headers is ignored, so a client reaching the
/// app directly can't pose as another address to get round per-client limits.
/// </summary>
public static class ProxyTrust
{
    /// <summary>
    /// When Proxy:KnownNetworks isn't set: the machine itself and the private networks, where Docker, Kubernetes and
    /// cloud load balancers put the proxy. The app must then not be reachable from those networks except through it.
    /// </summary>
    public const string DefaultNetworks = "127.0.0.0/8,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,fc00::/7";

    /// <summary>The trusted networks from a comma-separated list of CIDR ranges (a single address is a /32 or /128).</summary>
    public static IReadOnlyList<IPNetwork> Networks(string? configured)
    {
        var text = string.IsNullOrWhiteSpace(configured) ? DefaultNetworks : configured;
        var networks = new List<IPNetwork>();
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(part, out var network))
                networks.Add(network);
            else if (IPAddress.TryParse(part, out var address))
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            else
                throw new InvalidOperationException($"Proxy:KnownNetworks: '{part}' is not an address or a CIDR range (e.g. 172.18.0.0/16).");
        }
        return networks;
    }
}
