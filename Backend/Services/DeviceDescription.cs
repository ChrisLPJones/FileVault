namespace Backend.Services;

// A short description of the device behind a request, for the active sessions list
public static class DeviceDescription
{
    // "Chrome on Windows", "Safari on iPhone", "FileVault desktop app on macOS", ...
    // Only a rough guide: the User-Agent header is whatever the client chooses to send.
    public static string FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Unknown device";

        var ua = userAgent;
        bool Has(string s) => ua.Contains(s, StringComparison.OrdinalIgnoreCase);

        var browser =
            Has("Electron/") ? "FileVault desktop app" :
            Has("Edg/") || Has("EdgA/") || Has("EdgiOS/") ? "Edge" :
            Has("OPR/") || Has("Opera") ? "Opera" :
            Has("SamsungBrowser/") ? "Samsung Internet" :
            Has("Firefox/") || Has("FxiOS/") ? "Firefox" :
            Has("Chrome/") || Has("CriOS/") || Has("Chromium/") ? "Chrome" :
            Has("Safari/") && Has("Version/") ? "Safari" :
            Has("curl/") ? "curl" :
            Has("PostmanRuntime/") ? "Postman" :
            null;

        var os =
            Has("iPhone") ? "iPhone" :
            Has("iPad") ? "iPad" :
            Has("Android") ? "Android" :
            Has("CrOS") ? "ChromeOS" :
            Has("Windows") ? "Windows" :
            Has("Mac OS X") || Has("Macintosh") ? "macOS" :
            Has("Linux") ? "Linux" :
            null;

        var description = (browser, os) switch
        {
            (not null, not null) => $"{browser} on {os}",
            (not null, null) => browser,
            (null, not null) => $"Browser on {os}",
            _ => "Unknown device",
        };

        return description.Length <= 100 ? description : description[..100];
    }

    // The client's address as text (IPv4 addresses without the IPv6 "::ffff:" prefix)
    public static string? IpAddress(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress;
        if (address == null)
            return null;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return address.ToString();
    }
}
