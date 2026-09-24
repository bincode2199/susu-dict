using System.Net;
using System.Net.Sockets;

namespace Susu.Net;

/// <summary>
/// Cloud-origin address policy (PLAN 4.5.4.2): a cloud request must not resolve to loopback, private,
/// link-local, carrier-grade-NAT, unique-local or otherwise reserved space - only an explicitly
/// approved local origin (127.0.0.1:8765 Ollama, 127.0.0.1:11434 AnkiConnect, etc.) may target those.
/// Checked against every candidate address DNS returns, not just the one ultimately connected to, so a
/// multi-A-record host cannot slip a private address past the check (S05).
/// </summary>
public static class OriginPolicy
{
    public static bool IsDisallowedForCloud(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsDisallowedIPv4(address),
            AddressFamily.InterNetworkV6 => IsDisallowedIPv6(address),
            _ => true, // unknown family: never allow
        };
    }

    private static bool IsDisallowedIPv4(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        // 0.0.0.0/8, 10/8, 100.64/10 (CGN), 127/8 (loopback, already caught), 169.254/16 (link-local),
        // 172.16/12, 192.0.0.0/24 (IETF protocol), 192.0.2.0/24 (TEST-NET-1), 192.168/16,
        // 198.18.0.0/15 (benchmark), 198.51.100.0/24, 203.0.113.0/24 (TEST-NETs), 224+/4 (multicast/reserved).
        if (b[0] == 0) return true;
        if (b[0] == 10) return true;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;
        if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;
        if (b[0] >= 224) return true; // multicast (224-239) and reserved (240-255)
        return false;
    }

    private static bool IsDisallowedIPv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
        byte[] b = address.GetAddressBytes();
        if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique local
        if (b.All(x => x == 0)) return true; // ::
        return false;
    }
}

/// <summary>
/// An origin explicitly approved for plain HTTP + private/loopback addresses (installed/configured by
/// the user, PLAN 4.5.4.2: "本机 Ollama、AnkiConnect 可在安装／配置时明确批准精确 loopback HTTP origin").
/// Never inferred from a request; only ever populated by host-side configuration/installation code.
/// </summary>
public sealed class ApprovedLocalOrigins
{
    private readonly HashSet<string> origins = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    public void Approve(string origin) { lock (gate) origins.Add(origin); }
    public void Revoke(string origin) { lock (gate) origins.Remove(origin); }
    public bool Contains(string origin) { lock (gate) return origins.Contains(origin); }
}
