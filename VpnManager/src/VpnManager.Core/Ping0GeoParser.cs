using System.Net;

namespace VpnManager.Core;

public sealed record ExitGeo(string Ip, string Country, string Location);

public static class Ping0GeoParser
{
    public static ExitGeo? Parse(string response)
    {
        var lines = response.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !IPAddress.TryParse(lines[0], out _)) return null;
        var location = lines[1];
        var country = location.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(country) ? null : new ExitGeo(lines[0], country, location);
    }
}
