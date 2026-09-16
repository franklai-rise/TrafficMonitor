using System.Net.Http;
using System.Text.Json;

namespace VpnManager.Core;

public sealed class ClashControllerResolver
{
    private readonly string _configPath;
    public ClashControllerResolver(string configPath) => _configPath = configPath;
    public async Task<(string? Node, string? Country)> TryResolveAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configPath)) return (null, null);
        var settings = ParseConfig(File.ReadLines(_configPath));
        if (settings.Controller is null) return (null, null);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{settings.Controller}/proxies");
            if (!string.IsNullOrWhiteSpace(settings.Secret)) request.Headers.Add("Authorization", $"Bearer {settings.Secret}");
            using var response = await client.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("proxies", out var proxies)) return (null, null);
            var node = FindSelectedNode(proxies); return (node, InferCountry(node));
        }
        catch { return (null, null); }
    }
    private static (string? Controller, string? Secret) ParseConfig(IEnumerable<string> lines)
    {
        string? controller = null, secret = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim(); if (line.StartsWith("external-controller:", StringComparison.OrdinalIgnoreCase)) controller = Value(line);
            if (line.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)) secret = Value(line);
        }
        return (controller, secret);
    }
    private static string Value(string line) => line[(line.IndexOf(':') + 1)..].Trim().Trim('"', '\'');
    private static string? FindSelectedNode(JsonElement proxies)
    {
        foreach (var selector in new[] { "GLOBAL", "Proxy", "节点选择", "PROXY" })
            if (proxies.TryGetProperty(selector, out var group) && group.TryGetProperty("now", out var now) && now.ValueKind == JsonValueKind.String) return now.GetString();
        foreach (var property in proxies.EnumerateObject())
            if (property.Value.TryGetProperty("type", out var type) && type.GetString() == "Selector" && property.Value.TryGetProperty("now", out var now) && now.ValueKind == JsonValueKind.String) return now.GetString();
        return null;
    }
    private static string? InferCountry(string? node)
    {
        if (string.IsNullOrWhiteSpace(node)) return null; var value = node.ToLowerInvariant();
        return value.Contains("日本") || value.Contains(" japan") || value.Contains(" jp") ? "日本" : value.Contains("美国") || value.Contains(" united states") || value.Contains(" us") ? "美国" : value.Contains("香港") || value.Contains(" hong kong") || value.Contains(" hk") ? "香港" : value.Contains("德国") || value.Contains(" germany") || value.Contains(" de") ? "德国" : value.Contains("荷兰") || value.Contains(" netherlands") || value.Contains(" nl") ? "荷兰" : null;
    }
}
