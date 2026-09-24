using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexRadarHost;

public sealed class RadarCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string DirectoryPath { get; }
    public string CachePath => Path.Combine(DirectoryPath, "radar-cache.json");
    public string StatusPath => Path.Combine(DirectoryPath, "status.ini");

    public RadarCacheStore(string? directoryPath = null)
    {
        DirectoryPath = directoryPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "CodexRadarTrafficMonitor");
    }

    public RadarCache Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return new RadarCache();
            var cache = JsonSerializer.Deserialize<RadarCache>(File.ReadAllText(CachePath), JsonOptions);
            if (cache is null || cache.SchemaVersion != 1) return new RadarCache();
            cache.Benchmarks = new Dictionary<string, BenchmarkSnapshot>(cache.Benchmarks ?? [], StringComparer.Ordinal);
            return cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            RadarLog.Write("无法读取雷达缓存", ex);
            return new RadarCache();
        }
    }

    public void Save(RadarCache cache)
    {
        Directory.CreateDirectory(DirectoryPath);
        cache.SavedAtUtc = DateTimeOffset.UtcNow;
        WriteAtomic(CachePath, JsonSerializer.Serialize(cache, JsonOptions), new UTF8Encoding(false));
        WriteStatus(cache);
    }

    public void WriteStatus(RadarCache cache)
    {
        var deepSwe = cache.Benchmarks.TryGetValue(RadarBenchmarks.DeepSwe, out var snapshot) ? snapshot : null;
        var stale = deepSwe is null || deepSwe.Stale ||
            deepSwe.FetchedAtUtc is null || DateTimeOffset.UtcNow - deepSwe.FetchedAtUtc.Value > TimeSpan.FromMinutes(5);
        var targets = new[] { ("gpt-6-astra", "Astra"), ("gpt-6-sol", "Sol"), ("gpt-6-luna", "Luna") };
        var models = targets.Select(target => deepSwe?.Models.FirstOrDefault(model => model.Id.Equals(target.Item1, StringComparison.OrdinalIgnoreCase))).ToArray();
        var weights = RankingWeightsStore.Load();
        var ranked = WeightedRanking.Calculate(deepSwe, weights);
        var selected = targets.Select(target => ranked.FirstOrDefault(row => row.ModelId.Equals(target.Item1, StringComparison.OrdinalIgnoreCase) && row.WeightedScore.HasValue)).ToArray();
        var best = ranked.FirstOrDefault(row => row.WeightedScore.HasValue);
        var recommendation = best is null ? "综合优选 暂无数据"
            : $"综合优选 {best.Model.Replace("GPT-6 ", "", StringComparison.OrdinalIgnoreCase)} {best.Effort} {best.WeightedScoreText}";
        var value = deepSwe is null
            ? "GPT 雷达同步中"
            : string.Join(" | ", targets.Select((target, index) => $"{target.Item2} {selected[index]?.WeightedScoreText ?? "暂无数据"} ({selected[index]?.Effort ?? "-"})"));
        var compact = deepSwe is null ? "雷达同步中"
            : string.Join(" ", targets.Select((target, index) => $"{target.Item2[0]}{CompactScore(selected[index])}{CompactEffort(selected[index])}"));
        if (stale && deepSwe is not null) { value = "! " + value; compact = "! " + compact; }

        var lines = new List<string> { "众测雷达 · DeepSWE" };
        for (var index = 0; index < targets.Length; index++)
        {
            var row = selected[index];
            var summary = models[index];
            var samples = row?.Total is > 0 ? $"样本 {FormatInteger(row.Passed)} / {FormatInteger(row.Total)}" : "暂无有效样本";
            lines.Add($"GPT-6 {targets[index].Item2}：综合 {row?.WeightedScoreText ?? "暂无数据"}（{row?.Effort ?? "无有效档位"}） · IQ {row?.IqText ?? "暂无数据"} · {samples} · 参考价 ${row?.PriceText ?? "暂无数据"}/题 · 耗时 {row?.MinutesText ?? "暂无数据"} 分钟/题 · 汇总 IQ {FormatScore(summary?.Score)}");
        }
        lines.Add($"本机读取：{FormatLocal(deepSwe?.FetchedAtUtc)} · 来源更新：{FormatLocal(ParseTimestamp(deepSwe?.SourceUpdatedAt))}");
        lines.Add($"价格:IQ:速度权重 {weights.Price}:{weights.Iq}:{weights.Speed} · {recommendation} · 同频道相对比较");
        lines.Add("任务栏分数取整；档位 l=low、m=medium、h=high、x=xhigh，详细分数见上方。");
        if (stale) lines.Add(string.IsNullOrWhiteSpace(deepSwe?.RefreshError) ? "数据已过期，后台会自动重试。" : $"更新失败：{OneLine(deepSwe.RefreshError)}；正在保留上次成绩。");
        else lines.Add("后台每 60 秒读取公开数据。");

        var text = new StringBuilder()
            .AppendLine("[status]")
            .Append("value=").AppendLine(IniSafe(value))
            .Append("compact=").AppendLine(IniSafe(compact))
            .Append("recommendation=").AppendLine(IniSafe(recommendation))
            .Append("tooltip=").AppendLine(IniSafe(string.Join(" | ", lines)))
            .Append("stale=").AppendLine(stale ? "1" : "0")
            .Append("updatedUnixSeconds=").AppendLine((deepSwe?.FetchedAtUtc?.ToUnixTimeSeconds() ?? 0).ToString(CultureInfo.InvariantCulture))
            .ToString();
        Directory.CreateDirectory(DirectoryPath);
        WriteAtomic(StatusPath, text, Encoding.Unicode);
    }

    private static void WriteAtomic(string path, string contents, Encoding encoding)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null, ignoreMetadataErrors: true);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string FormatScore(double? score) => score.HasValue ? score.Value.ToString("0.0", CultureInfo.InvariantCulture) : "暂无";
    private static string CompactScore(RankedEffortRow? row) => row?.WeightedScore is double score
        ? Math.Round(score, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
        : "--";
    private static string CompactEffort(RankedEffortRow? row) => row?.Effort?.ToLowerInvariant() switch
    {
        "low" => "l",
        "medium" => "m",
        "high" => "h",
        "xhigh" => "x",
        _ => ""
    };
    private static string FormatInteger(int? number) => number?.ToString("N0", CultureInfo.GetCultureInfo("zh-CN")) ?? "0";
    private static DateTimeOffset? ParseTimestamp(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp : null;
    private static string FormatLocal(DateTimeOffset? value) => value.HasValue ? value.Value.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN")) : "暂无";
    private static string OneLine(string? value) => (value ?? "未知错误").Replace('\r', ' ').Replace('\n', ' ').Trim();
    private static string IniSafe(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public static class RadarLog
{
    private static readonly object Gate = new();
    private static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "CodexRadarTrafficMonitor", "logs", "radar.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                    File.Move(LogPath, LogPath + ".previous", overwrite: true);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{(exception is null ? "" : $"：{exception.Message}")}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch { }
    }
}
