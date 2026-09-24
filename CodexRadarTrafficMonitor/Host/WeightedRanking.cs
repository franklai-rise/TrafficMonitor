using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CodexRadarHost;

public sealed record RankingWeights(int Price = 3, int Iq = 3, int Speed = 4)
{
    public bool IsValid => Price is >= 0 and <= 10 && Iq is >= 0 and <= 10 && Speed is >= 0 and <= 10 && Price + Iq + Speed > 0;
}

public sealed class RankedEffortRow
{
    public string ModelId { get; init; } = "";
    public string Model { get; init; } = "";
    public string Effort { get; init; } = "";
    public double? Iq { get; init; }
    public int? Passed { get; init; }
    public int? Total { get; init; }
    public double? Price { get; init; }
    public double? Minutes { get; init; }
    public double? WeightedScore { get; init; }
    public string? SourceUpdatedAt { get; init; }
    public string IqText => Iq?.ToString("0.##", CultureInfo.InvariantCulture) ?? "暂无数据";
    public string SamplesText => Total?.ToString("N0", CultureInfo.GetCultureInfo("zh-CN")) ?? "暂无数据";
    public string PriceText => Price?.ToString("0.######", CultureInfo.InvariantCulture) ?? "暂无数据";
    public string MinutesText => Minutes?.ToString("0.##", CultureInfo.InvariantCulture) ?? "暂无数据";
    public string WeightedScoreText => WeightedScore?.ToString("0.0", CultureInfo.InvariantCulture) ?? "暂无数据";
    public string SourceText => DateTimeOffset.TryParse(SourceUpdatedAt, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var time)
        ? time.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN")) : "暂无";
}

public static class WeightedRanking
{
    public static List<RankedEffortRow> Calculate(BenchmarkSnapshot? snapshot, RankingWeights weights)
    {
        if (!weights.IsValid) throw new ArgumentOutOfRangeException(nameof(weights));
        if (snapshot is null) return [];
        var rows = snapshot.Models.SelectMany(model => model.Efforts.Select(effort => new RankedEffortRow
        {
            ModelId = model.Id,
            Model = PrettyModel(model.Id),
            Effort = effort.Effort,
            Iq = Valid(effort.Score),
            Passed = effort.Passed,
            Total = effort.Total is > 0 ? effort.Total : null,
            Price = Valid(effort.CostUsd),
            Minutes = Valid(effort.Minutes),
            SourceUpdatedAt = effort.SourceUpdatedAt
        })).ToList();
        var complete = rows.Where(row => row.Iq.HasValue && row.Total is > 0 && row.Price is >= 0 && row.Minutes is > 0).ToList();
        if (complete.Count == 0) return rows;
        var iqMin = complete.Min(row => row.Iq!.Value);
        var iqMax = complete.Max(row => row.Iq!.Value);
        var priceMin = complete.Min(row => row.Price!.Value);
        var priceMax = complete.Max(row => row.Price!.Value);
        var timeMin = complete.Min(row => row.Minutes!.Value);
        var timeMax = complete.Max(row => row.Minutes!.Value);
        var totalWeight = weights.Price + weights.Iq + weights.Speed;
        return rows.Select(row => new RankedEffortRow
        {
            ModelId = row.ModelId,
            Model = row.Model,
            Effort = row.Effort,
            Iq = row.Iq,
            Passed = row.Passed,
            Total = row.Total,
            Price = row.Price,
            Minutes = row.Minutes,
            SourceUpdatedAt = row.SourceUpdatedAt,
            WeightedScore = row.Iq.HasValue && row.Total is > 0 && row.Price is >= 0 && row.Minutes is > 0
                ? Math.Round(100 * (weights.Price * Lower(row.Price.Value, priceMin, priceMax)
                    + weights.Iq * Higher(row.Iq.Value, iqMin, iqMax)
                    + weights.Speed * Lower(row.Minutes.Value, timeMin, timeMax)) / totalWeight, 1)
                : null
        }).OrderByDescending(row => row.WeightedScore ?? -1).ThenBy(row => row.ModelId, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Effort, StringComparer.Ordinal).ToList();
    }

    private static double? Valid(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value : null;
    private static double Higher(double value, double min, double max) => max == min ? 1 : (value - min) / (max - min);
    private static double Lower(double value, double min, double max) => max == min ? 1 : (max - value) / (max - min);
    private static string PrettyModel(string id) => id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
        ? "GPT-" + string.Join(" ", id[4..].Split('-').Select(piece => piece.Length == 1
            ? piece.ToUpperInvariant() : char.ToUpperInvariant(piece[0]) + piece[1..])) : id;
}

public static class RankingWeightsStore
{
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "Local", "CodexRadarTrafficMonitor", "ranking-weights.json");

    public static RankingWeights Load()
    {
        try
        {
            var weights = JsonSerializer.Deserialize<RankingWeights>(File.ReadAllText(PathName));
            return weights is { IsValid: true } ? weights : new RankingWeights();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new RankingWeights();
        }
    }

    public static void Save(RankingWeights weights)
    {
        if (!weights.IsValid) return;
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var temp = PathName + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(weights));
            if (File.Exists(PathName)) File.Replace(temp, PathName, null);
            else File.Move(temp, PathName);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
