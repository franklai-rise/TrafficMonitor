using System.Text.Json.Serialization;

namespace CodexRadarHost;

public static class RadarBenchmarks
{
    public const string DeepSwe = "deep-swe";
    public const string Pompeii = "pompeii-adjacency";
    public static readonly string[] All = [DeepSwe, Pompeii];

    public static string Title(string id) => id == Pompeii ? "视觉推理 · 庞贝壁画" : "软件工程 · DeepSWE";
}

public sealed class RadarEffort
{
    public string Effort { get; set; } = "";
    public double? Score { get; set; }
    public int? Passed { get; set; }
    public int? Total { get; set; }
    public int? RunsTotal { get; set; }
    public int? Runs24H { get; set; }
    public int? Runs48H { get; set; }
    public double? Minutes { get; set; }
    public double? CostUsd { get; set; }
    public double? AgentSteps { get; set; }
    public double? TotalTokens { get; set; }
    public double? CacheHitRate { get; set; }
    public string? SourceUpdatedAt { get; set; }
}

public sealed class RadarModel
{
    public string Id { get; set; } = "";
    public double? Score { get; set; }
    public int? Passed { get; set; }
    public int? Total { get; set; }
    public string? SourceUpdatedAt { get; set; }
    public List<RadarEffort> Efforts { get; set; } = [];
}

public sealed class BenchmarkSnapshot
{
    public string Benchmark { get; set; } = "";
    public string BenchmarkTitle { get; set; } = "";
    public string ScoreLabel { get; set; } = "IQ";
    public string ScoringMode { get; set; } = "equal_latest_3";
    public string? SourceUpdatedAt { get; set; }
    public DateTimeOffset? FetchedAtUtc { get; set; }
    public bool Stale { get; set; }
    public string? RefreshError { get; set; }
    public List<RadarModel> Models { get; set; } = [];
}

public sealed class RadarCache
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, BenchmarkSnapshot> Benchmarks { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RadarModelRow
{
    public string ModelId { get; init; } = "";
    public string Model { get; init; } = "";
    public double? Score { get; init; }
    public int? Passed { get; init; }
    public int? Total { get; init; }
    public string ScoreText => Score.HasValue ? Score.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "暂无数据";
    public string PassedText => Passed?.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("zh-CN")) ?? "暂无数据";
    public string TotalText => Total?.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("zh-CN")) ?? "暂无数据";
    public string? Updated { get; init; }
    public List<RadarEffortRow> Efforts { get; init; } = [];
}

public sealed class RadarEffortRow
{
    public string Effort { get; init; } = "";
    public double? Score { get; init; }
    public int? Passed { get; init; }
    public int? Total { get; init; }
    public int? RunsTotal { get; init; }
    public int? Runs24H { get; init; }
    public int? Runs48H { get; init; }
    public string ScoreText => Score.HasValue ? Score.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "暂无数据";
    public string Samples => Passed.HasValue && Total is > 0
        ? $"{Passed.Value.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"))} / {Total.Value.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"))}"
        : "暂无数据";
    public string RunsText => RunsTotal?.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("zh-CN")) ?? "暂无数据";
    public double? CostUsd { get; init; }
    public string CostText => CostUsd.HasValue ? CostUsd.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) : "暂无数据";
    public double? Minutes { get; init; }
    public string MinutesText => Minutes.HasValue ? Minutes.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "暂无数据";
    public double? AgentSteps { get; init; }
    public string AgentStepsText => AgentSteps.HasValue ? AgentSteps.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "暂无数据";
    public double? TotalTokens { get; init; }
    public double? CacheHitRate { get; init; }
    public string? SourceUpdatedAt { get; init; }
    public string AdditionalStats => string.Join(" · ", new[]
    {
        $"24h {FormatInteger(Runs24H)} / 48h {FormatInteger(Runs48H)}",
        TotalTokens.HasValue ? $"Token {TotalTokens.Value.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"))}" : "Token 暂无数据",
        CacheHitRate.HasValue ? $"缓存命中 {CacheHitRate.Value:P1}" : "缓存命中暂无数据"
    });

    private static string FormatInteger(int? value) => value?.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("zh-CN")) ?? "暂无数据";
}
