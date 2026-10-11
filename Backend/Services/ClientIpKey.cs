using System.Net;
using System.Net.Sockets;

namespace Backend.Services;

// The rate-limit key for a client address: IPv4-mapped IPv6 is treated as plain IPv4, and a real IPv6
// address is keyed on its /64 (one subscriber usually owns a whole /64, so per-address keys are free to rotate).
public static class ClientIpKey
{
    public const string Unknown = "unknown";

    public static string From(IPAddress? address)
    {
        if (address == null)
            return Unknown;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        return $"{Convert.ToHexString(bytes.AsSpan(0, 8))}::/64";
    }
}

// Start-up check of the sign-up rate limit settings: 0 or less is not "off", it makes every sign-up fail
public static class SignupRateLimit
{
    public static void Validate(IConfiguration config, bool hosted)
    {
        // Every policy that reads RateLimiting:*, not just sign-ups
        var policies = hosted ? new[] { "auth", "refresh", "two-factor", "signup" } : ["auth", "refresh", "two-factor"];
        foreach (var (policy, name) in policies.SelectMany(p => new[] { "PermitLimit", "WindowSeconds" }.Select(n => (p, n))))
        {
            var key = $"RateLimiting:{policy}:{name}";
            // An unset value uses the default; a value that isn't a number fails in GetValue
            var value = config.GetValue<int?>(key);
            if (value is <= 0)
                throw new InvalidOperationException(
                    $"{key} must be a whole number greater than 0 (0 does not turn the limit off), but it is {value}.");
        }
    }
}
