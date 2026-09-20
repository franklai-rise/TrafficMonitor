using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VpnManager.Core;

public sealed class ClashControllerResolver
{
    private readonly string _configPath;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    public ClashControllerResolver(string configPath, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _configPath = configPath;
        _handlerFactory = handlerFactory ?? (() => new HttpClientHandler { UseProxy = false });
    }
    public async Task<(string? Node, string? Country)> TryResolveAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_configPath)) return (null, null);
            string? controller = null, secret = null;
            foreach (var line in File.ReadLines(_configPath))
            {
                // Only top-level controller settings; never read a proxy's nested secret.
                if (line.StartsWith("external-controller:", StringComparison.OrdinalIgnoreCase)) controller = Value(line);
                if (line.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)) secret = Value(line);
            }
            if (!Uri.TryCreate("http://" + controller, UriKind.Absolute, out var address) ||
                address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0) return (null, null);
            var host = address.DnsSafeHost.Trim('[', ']');
            if (host == "0.0.0.0" || host == "::") address = new UriBuilder(address) { Host = "127.0.0.1" }.Uri;
            else if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                     !(IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))) return (null, null);
            using var client = new HttpClient(_handlerFactory()) { Timeout = TimeSpan.FromSeconds(3), BaseAddress = address };
            if (!string.IsNullOrWhiteSpace(secret)) client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
            using var config = JsonDocument.Parse(await client.GetStringAsync("/configs", cancellationToken));
            var mode = config.RootElement.TryGetProperty("mode", out var m) ? m.GetString()?.ToLowerInvariant() : null;
            if (mode == "rule") return ("规则分流", null);
            if (mode == "direct") return ("直连规则", null);
            if (mode != "global") return (null, null);
            using var json = JsonDocument.Parse(await client.GetStringAsync("/proxies", cancellationToken));
            if (!json.RootElement.TryGetProperty("proxies", out var proxies)) return (null, null);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var node = "GLOBAL";
            for (var depth = 0; depth < 16; depth++)
            {
                if (!seen.Add(node) || !proxies.TryGetProperty(node, out var value)) return (null, null);
                if (!value.TryGetProperty("now", out var selected)) return (node, InferCountry(node));
                if (selected.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(selected.GetString())) return (null, null);
                node = selected.GetString()!;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or ArgumentException or OperationCanceledException) { }
        return (null, null);
    }
    private static string Value(string line)
    {
        var value = line[(line.IndexOf(':') + 1)..].Trim();
        if (value.StartsWith('"') || value.StartsWith('\'')) {
            var end = value.LastIndexOf(value[0]);
            return end > 0 ? value[1..end] : "";
        }
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? value[..comment] : value).Trim();
    }
    private static string? InferCountry(string node)
    {
        foreach (var (country, pattern) in new[] {
            ("日本", @"日本|🇯🇵|\b(jp|japan)\b"), ("美国", @"美国|🇺🇸|\b(us|usa|united states)\b"),
            ("香港", @"香港|🇭🇰|\b(hk|hong kong)\b"), ("德国", @"德国|🇩🇪|\b(de|germany)\b"),
            ("荷兰", @"荷兰|🇳🇱|\b(nl|netherlands)\b"), ("新加坡", @"新加坡|🇸🇬|\b(sg|singapore)\b"),
            ("英国", @"英国|🇬🇧|\b(uk|gb|united kingdom)\b") })
            if (Regex.IsMatch(node, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return country;
        return null;
    }
}
