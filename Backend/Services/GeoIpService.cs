using System.Net;
using System.Net.Sockets;
using MaxMind.GeoIP2;

namespace Backend.Services;

// Looks up where an IP address is, in local databases (nothing leaves the server). Two optional
// files, in order of preference: a City database (GeoIp:CityDatabasePath; DB-IP "IP to City Lite"
// or a MaxMind GeoLite2/GeoIP2 City file) giving country, region and city, then the MaxMind
// GeoLite2 Country database (GeoIp:DatabasePath) giving the country only. When neither file is
// usable every lookup returns null and the app carries on. Each file is re-opened when it changes
// on disk (the updater replaces it). Results are computed per request and never stored.
public sealed class GeoIpService
{
    private readonly Source? _city;
    private readonly Source? _country;
    private readonly ILogger<GeoIpService> _logger;
    private int _loggedCityFailure;

    public GeoIpService(string? databasePath, ILogger<GeoIpService> logger, TimeSpan? recheckInterval = null,
        string? cityDatabasePath = null)
    {
        _logger = logger;
        var interval = recheckInterval ?? TimeSpan.FromHours(1);
        _country = Source.Create(databasePath, interval, logger);
        _city = Source.Create(cityDatabasePath, interval, logger);
    }

    // The result of a lookup. City and Region are null for a country-only database or when the
    // database has none for the address. FromDbIp is true when the answer came from a DB-IP file,
    // whose CC BY 4.0 licence requires an attribution wherever it is shown.
    public sealed record GeoLocation(string IsoCode, string Country, string? City, string? Region, bool FromDbIp);

    // Whether any database is currently loaded
    public bool IsAvailable => _city?.Reader() != null || _country?.Reader() != null;

    // DB-IP files report a database type such as "DBIP-City-Lite"
    public static bool IsDbIpType(string? databaseType) =>
        databaseType != null && databaseType.StartsWith("DBIP", StringComparison.OrdinalIgnoreCase);

    // The location of a public address, or null (no database, private/unparseable address, or not
    // in the database)
    public GeoLocation? TryLookup(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress, out var ip))
            return null;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (IsNonPublic(ip))
            return null;

        try
        {
            var city = _city?.Reader();
            if (city != null && city.TryCity(ip, out var c) && c?.Country?.IsoCode is { } cityCode)
            {
                return new GeoLocation(
                    cityCode,
                    c.Country.Name ?? cityCode,
                    NullIfBlank(c.City?.Name),
                    NullIfBlank(c.Subdivisions.FirstOrDefault()?.Name),
                    IsDbIpType(city.Metadata.DatabaseType));
            }
        }
        catch (Exception ex)
        {
            // e.g. the file is not a City database: fall back to the Country file
            if (Interlocked.Exchange(ref _loggedCityFailure, 1) == 0)
                _logger.LogWarning(ex, "GeoIP city lookup failed (logged once); using the Country database");
        }

        try
        {
            // Not in the City file (or none installed): the Country file gives the country alone
            var country = _country?.Reader();
            if (country != null && country.TryCountry(ip, out var r) && r?.Country?.IsoCode is { } code)
                return new GeoLocation(code, r.Country.Name ?? code, null, null, IsDbIpType(country.Metadata.DatabaseType));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GeoIP lookup failed");
        }
        return null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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

    // One database file: opened the first time it is needed, and again when its last-write time changes
    // (checked at most hourly)
    private sealed class Source
    {
        private readonly string _path;
        private readonly ILogger _logger;
        private readonly TimeSpan _recheckInterval;
        private readonly object _gate = new();

        private DatabaseReader? _reader;
        private DateTime _loadedWriteTime;
        private DateTime _lastCheck = DateTime.MinValue;
        private bool _loggedProblem;

        private Source(string path, TimeSpan recheckInterval, ILogger logger)
        {
            _path = path;
            _recheckInterval = recheckInterval;
            _logger = logger;
        }

        public static Source? Create(string? path, TimeSpan recheckInterval, ILogger logger) =>
            string.IsNullOrWhiteSpace(path) ? null : new Source(path.Trim(), recheckInterval, logger);

        public DatabaseReader? Reader()
        {
            Refresh();
            return _reader;
        }

        private void Refresh()
        {
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
                            _logger.LogInformation("No GeoIP database at {Path}; it is skipped", _path);
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
                        _logger.LogWarning(ex, "Could not read the GeoIP database at {Path}; it is skipped", _path);
                    _loggedProblem = true;
                }
            }
        }
    }
}
