using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace CodexRadarHost;

public sealed class RadarService
{
    private static readonly Uri ApiRoot = new("https://api.codexradar.com/api/v1/");
    private static readonly string[] EffortOrder = ["low", "medium", "high", "xhigh", "max", "ultra", "off"];
    private readonly HttpClient _http;

    public RadarService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(24) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TrafficMonitor-GPT-Radar/1.0");
    }

    public async Task<BenchmarkSnapshot> FetchAsync(string benchmark, CancellationToken cancellationToken = default)
    {
        if (!RadarBenchmarks.All.Contains(benchmark, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(benchmark), "不支持的评测频道。");

        var encoded = Uri.EscapeDataString(benchmark);
        var tableTask = DownloadJsonAsync(new Uri(ApiRoot, $"table?benchmark={encoded}&ui=traffic-monitor-v1"), cancellationToken);
        var scoreTask = DownloadJsonAsync(new Uri(ApiRoot, $"intelligence-efficiency?benchmark={encoded}&v=traffic-monitor-v1"), cancellationToken);
        await Task.WhenAll(tableTask, scoreTask).ConfigureAwait(false);
        return Normalize(benchmark, await tableTask.ConfigureAwait(false), await scoreTask.ConfigureAwait(false), DateTimeOffset.UtcNow);
    }

    private async Task<JsonDocument> DownloadJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static BenchmarkSnapshot Normalize(string benchmark, JsonDocument table, JsonDocument efficiency, DateTimeOffset fetchedAtUtc)
    {
        var scoreRoot = efficiency.RootElement;
        if (GetString(scoreRoot, "benchmark_id") != benchmark)
            throw new InvalidDataException("雷达接口返回了不同评测频道的数据。");
        var mode = GetString(scoreRoot, "mode");
        if (mode != "equal_latest_3")
            throw new InvalidDataException($"雷达评分口径已变化（{mode}），缓存未更新。");

        var allowed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (!table.RootElement.TryGetProperty("combos", out var combos) || combos.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("雷达模型列表格式不正确。");
        foreach (var combo in combos.EnumerateArray())
        {
            var model = GetString(combo, "model");
            var effort = GetString(combo, "effort");
            if (!model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || effort.Length == 0 ||
                GetString(combo, "agent").Length != 0 || GetString(combo, "provider").Length != 0 ||
                GetBool(combo, "manual_only")) continue;
            if (!allowed.TryGetValue(model, out var efforts)) allowed[model] = efforts = new(StringComparer.Ordinal);
            efforts.Add(effort);
        }

        var points = new Dictionary<(string Model, string Effort), JsonElement>();
        if (!scoreRoot.TryGetProperty("points", out var scorePoints) || scorePoints.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("雷达成绩列表格式不正确。");
        foreach (var point in scorePoints.EnumerateArray())
        {
            var model = GetString(point, "model");
            var effort = GetString(point, "effort");
            if (model.Length != 0 && effort.Length != 0) points[(model, effort)] = point;
        }

        var models = new List<RadarModel>();
        foreach (var (modelId, effortNames) in allowed.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var model = new RadarModel { Id = modelId };
            foreach (var effortName in effortNames.OrderBy(EffortRank).ThenBy(value => value, StringComparer.Ordinal))
            {
                points.TryGetValue((modelId, effortName), out var point);
                var effort = new RadarEffort { Effort = effortName };
                if (point.ValueKind == JsonValueKind.Object)
                {
                    effort.Passed = GetInt(point, "passed");
                    effort.Total = GetInt(point, "total");
                    effort.Score = GetDouble(point, "iq");
                    if (!effort.Score.HasValue && effort.Passed.HasValue && effort.Total > 0)
                        effort.Score = ScoreFromSamples(effort.Passed.Value, effort.Total.Value);
                    effort.RunsTotal = GetInt(point, "runs_total");
                    effort.Runs24H = GetInt(point, "runs_24h");
                    effort.Runs48H = GetInt(point, "runs_48h");
                    effort.Minutes = GetDouble(point, "average_minutes");
                    effort.CostUsd = GetDouble(point, "average_price_usd");
                    effort.AgentSteps = GetDouble(point, "average_agent_steps");
                    effort.TotalTokens = GetDouble(point, "average_total_tokens");
                    effort.CacheHitRate = GetDouble(point, "cache_hit_rate");
                    effort.SourceUpdatedAt = GetStringOrNull(point, "source_updated_at");
                }
                model.Efforts.Add(effort);
            }

            var measured = model.Efforts.Where(row => row.Passed.HasValue && row.Total is > 0).ToList();
            var total = measured.Sum(row => row.Total!.Value);
            if (total > 0)
            {
                model.Passed = measured.Sum(row => row.Passed!.Value);
                model.Total = total;
                model.Score = ScoreFromSamples(model.Passed.Value, total);
            }
            model.SourceUpdatedAt = NewestTimestamp(model.Efforts.Select(row => row.SourceUpdatedAt));
            models.Add(model);
        }

        var sourceUpdatedAt = GetStringOrNull(scoreRoot, "source_updated_at");
        return new BenchmarkSnapshot
        {
            Benchmark = benchmark,
            BenchmarkTitle = RadarBenchmarks.Title(benchmark),
            ScoreLabel = GetString(scoreRoot, "score_label") is { Length: > 0 } label ? label : "IQ",
            ScoringMode = mode,
            SourceUpdatedAt = sourceUpdatedAt,
            FetchedAtUtc = fetchedAtUtc,
            Stale = false,
            Models = models
        };
    }

    public static double ScoreFromSamples(int passed, int total) => Math.Round(passed * 150.0 / total, 2, MidpointRounding.AwayFromZero);

    private static int EffortRank(string effort)
    {
        var index = Array.IndexOf(EffortOrder, effort);
        return index < 0 ? EffortOrder.Length : index;
    }

    private static string? NewestTimestamp(IEnumerable<string?> timestamps) => timestamps
        .Where(value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
        .OrderByDescending(value => DateTimeOffset.Parse(value!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal))
        .FirstOrDefault();

    private static string GetString(JsonElement value, string name) => GetStringOrNull(value, name) ?? "";
    private static string? GetStringOrNull(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
    private static bool GetBool(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static double? GetDouble(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number)
            ? number : null;
    private static int? GetInt(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
            ? number : null;
}
