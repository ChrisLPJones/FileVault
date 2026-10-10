using System.Net;
using System.Net.Sockets;
using MaxMind.GeoIP2;

namespace Backend.Services;

// Looks up the country of an IP address in a local MaxMind GeoLite2 Country database
// (GeoIp:DatabasePath). Nothing leaves the server. The database is optional: when the file is
// missing or unreadable every lookup returns null and the app carries on. The file is re-opened
// when it changes on disk (the geoipupdate container replaces it every few days).
public sealed class GeoIpService
{
    private readonly string? _path;
    private readonly ILogger<GeoIpService> _logger;
    private readonly TimeSpan _recheckInterval;
    private readonly object _gate = new();

    private DatabaseReader? _reader;
    private DateTime _loadedWriteTime;
    private DateTime _lastCheck = DateTime.MinValue;
    private bool _loggedProblem;

    public GeoIpService(string? databasePath, ILogger<GeoIpService> logger, TimeSpan? recheckInterval = null)
    {
        _path = string.IsNullOrWhiteSpace(databasePath) ? null : databasePath.Trim();
        _logger = logger;
        _recheckInterval = recheckInterval ?? TimeSpan.FromHours(1);
    }

    // Whether a database is currently loaded
    public bool IsAvailable
    {
        get
        {
            Refresh();
            return _reader != null;
        }
    }

    // The ISO country code and English name for a public address, or null (no database,
    // private/unparseable address, or not in the database)
    public (string IsoCode, string Name)? TryCountry(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress, out var ip))
            return null;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (IsNonPublic(ip))
            return null;

        Refresh();
        var reader = _reader;
        if (reader == null)
            return null;

        try
        {
            if (reader.TryCountry(ip, out var response) && response?.Country?.IsoCode is { } code)
                return (code, response.Country.Name ?? code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GeoIP lookup failed");
        }
        return null;
    }

    // Loopback, private, link-local, carrier-grade NAT and other addresses with no country
    internal static bool IsNonPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] >= 224; // multicast and reserved
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC; // unique local fc00::/7
        }

        return true;
    }

    // Opens the file the first time, and again when its last-write time changes (checked at most hourly)
    private void Refresh()
    {
        if (_path == null)
            return;

        var now = DateTime.UtcNow;
        if (_lastCheck != DateTime.MinValue && now - _lastCheck < _recheckInterval)
            return;

        lock (_gate)
        {
            if (_lastCheck != DateTime.MinValue && now - _lastCheck < _recheckInterval)
                return;
            _lastCheck = now;

            try
            {
                if (!File.Exists(_path))
                {
                    if (!_loggedProblem)
                    {
                        _logger.LogInformation("No GeoIP database at {Path}; login countries will show as unknown", _path);
                        _loggedProblem = true;
                    }
                    return; // keep serving an already-loaded copy
                }

                var writeTime = File.GetLastWriteTimeUtc(_path);
                if (_reader != null && writeTime == _loadedWriteTime)
                    return;

                // Memory mode reads the file once, so the file itself can be replaced afterwards.
                // The old reader is left for the GC: lookups on other threads may still be using it.
                _reader = new DatabaseReader(_path, MaxMind.Db.FileAccessMode.Memory);
                _loadedWriteTime = writeTime;
                _loggedProblem = false;
                _logger.LogInformation("Loaded GeoIP database {Path}", _path);
            }
            catch (Exception ex)
            {
                if (!_loggedProblem)
                    _logger.LogWarning(ex, "Could not read the GeoIP database at {Path}; login countries will show as unknown", _path);
                _loggedProblem = true;
            }
        }
    }
}
