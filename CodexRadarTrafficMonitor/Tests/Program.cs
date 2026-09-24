using System.Text;
using System.Text.Json;
using CodexRadarHost;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static BenchmarkSnapshot Normalize(string benchmark, string tableText, string scoreText)
{
    using var table = JsonDocument.Parse(tableText);
    using var score = JsonDocument.Parse(scoreText);
    return RadarService.Normalize(benchmark, table, score, DateTimeOffset.UtcNow);
}

var tableJson = """
{
  "combos": [
    {"model":"gpt-6-astra","effort":"high"},
    {"model":"gpt-6-astra","effort":"low"},
    {"model":"gpt-6-sol","effort":"medium"},
    {"model":"gpt-6-luna","effort":"low"},
    {"model":"gpt-6-foreign","effort":"low","provider":"openai"},
    {"model":"gpt-6-agent","effort":"low","agent":"DSH"},
    {"model":"claude-opus","effort":"high"}
  ]
}
""";
var scoreJson = """
{
  "benchmark_id":"deep-swe","mode":"equal_latest_3","score_label":"IQ",
  "source_updated_at":"2026-09-23T20:00:00+08:00",
  "points":[
    {"model":"gpt-6-astra","effort":"low","passed":2,"total":4,"iq":75,"runs_total":12,"average_price_usd":1.25,"average_minutes":6.5,"average_agent_steps":30,"average_total_tokens":15000,"cache_hit_rate":0.95,"source_updated_at":"2026-09-23T19:00:00+08:00"},
    {"model":"gpt-6-astra","effort":"high","passed":3,"total":4,"iq":112.5,"runs_total":16,"source_updated_at":"2026-09-23T20:00:00+08:00"},
    {"model":"gpt-6-sol","effort":"medium","passed":0,"total":0,"iq":null},
    {"model":"gpt-6-luna","effort":"low","passed":1,"total":4,"iq":37.5}
  ]
}
""";

var sample = Normalize(RadarBenchmarks.DeepSwe, tableJson, scoreJson);
var astra = sample.Models.Single(model => model.Id == "gpt-6-astra");
Check(astra.Score == 93.75 && astra.Passed == 5 && astra.Total == 8, "Astra summary must aggregate passed and total samples.");
Check(astra.Efforts.Select(row => row.Effort).SequenceEqual(["low", "high"]), "Efforts must follow the published intensity order.");
Check(astra.Efforts[0].CostUsd == 1.25 && astra.Efforts[0].Minutes == 6.5, "Public cost and duration fields must be preserved.");
Check(sample.Models.Count == 3, "Only native GPT rows should be included.");
var sol = sample.Models.Single(model => model.Id == "gpt-6-sol");
Check(sol.Score is null && sol.Efforts[0].Score is null, "Zero samples must remain unavailable, not become a score of zero.");

var failedCache = new RadarCache();
failedCache.Benchmarks[RadarBenchmarks.DeepSwe] = sample;
RadarCachePolicy.Apply(failedCache, RadarBenchmarks.DeepSwe, null, new HttpRequestException("offline"));
var stale = failedCache.Benchmarks[RadarBenchmarks.DeepSwe];
Check(stale.Stale && stale.Models.Single(model => model.Id == "gpt-6-astra").Score == 93.75, "Refresh failure must preserve prior scores and mark them stale.");
Check(stale.RefreshError == "网络连接失败", "Network failures should have a clear status.");

var unavailable = new RadarCache();
RadarCachePolicy.Apply(unavailable, RadarBenchmarks.Pompeii, null, new TaskCanceledException());
Check(unavailable.Benchmarks[RadarBenchmarks.Pompeii].Stale && unavailable.Benchmarks[RadarBenchmarks.Pompeii].Models.Count == 0,
    "First-load failures must produce an explicit unavailable snapshot.");

var incompatibleRejected = false;
try { _ = Normalize(RadarBenchmarks.DeepSwe, tableJson, scoreJson.Replace("equal_latest_3", "latest_per_cell", StringComparison.Ordinal)); }
catch (InvalidDataException) { incompatibleRejected = true; }
Check(incompatibleRejected, "An unknown scoring mode must not enter the cache.");
var wrongBenchmarkRejected = false;
try { _ = Normalize(RadarBenchmarks.Pompeii, tableJson, scoreJson); }
catch (InvalidDataException) { wrongBenchmarkRejected = true; }
Check(wrongBenchmarkRejected, "A response for another benchmark must not enter the cache.");

var rankingSample = new BenchmarkSnapshot { Benchmark = RadarBenchmarks.DeepSwe, Models =
[
    new RadarModel { Id = "gpt-6-astra", Efforts = [new RadarEffort { Effort = "high", Score = 100, Total = 10, CostUsd = 3, Minutes = 10 }] },
    new RadarModel { Id = "gpt-6-sol", Efforts = [new RadarEffort { Effort = "low", Score = 80, Total = 10, CostUsd = 1, Minutes = 5 }] },
    new RadarModel { Id = "gpt-6-luna", Efforts = [new RadarEffort { Effort = "medium", Score = 90, Total = 10, CostUsd = 2, Minutes = 8 },
        new RadarEffort { Effort = "high", Score = 95, Total = 0, CostUsd = 2, Minutes = 8 }] }
] };
var ranked = WeightedRanking.Calculate(rankingSample, new RankingWeights());
Check(ranked[0].ModelId == "gpt-6-sol" && ranked[0].WeightedScore == 70,
    "Default 3:3:4 weights must prefer the cheaper, faster Sol row in this fixture.");
Check(ranked.Single(row => row.ModelId == "gpt-6-astra").WeightedScore == 30,
    "The expensive, slow but highest-IQ row must retain its IQ contribution.");
Check(ranked.Single(row => row.ModelId == "gpt-6-luna" && row.Effort == "high").WeightedScore is null,
    "Zero-sample rows must not receive a weighted estimate.");
var iqOnly = WeightedRanking.Calculate(rankingSample, new RankingWeights(0, 10, 0));
Check(iqOnly[0].ModelId == "gpt-6-astra" && iqOnly[0].WeightedScore == 100,
    "Manual IQ-only weighting must change the ranking.");
Check(!new RankingWeights(0, 0, 0).IsValid, "All-zero weights must be rejected.");

var tempDirectory = Path.Combine(Path.GetTempPath(), "CodexRadarRegression-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new RadarCacheStore(tempDirectory);
    store.Save(failedCache);
    var reloaded = store.Load();
    Check(reloaded.Benchmarks[RadarBenchmarks.DeepSwe].Models.Single(model => model.Id == "gpt-6-astra").Score == 93.75,
        "The full model cache must survive a restart.");
    var ini = File.ReadAllText(store.StatusPath, Encoding.Unicode);
    Check(ini.Contains("! Astra 100.0 (low)", StringComparison.Ordinal) && ini.Contains("更新失败", StringComparison.Ordinal),
        "Taskbar status must mark stale cached data and show its refresh error.");
    Check(ini.Contains("样本 2 / 4", StringComparison.Ordinal) && ini.Contains("GPT-6 Luna", StringComparison.Ordinal),
        "Taskbar tooltip must include the selected effort's samples for the three GPT-6 models.");
    Check(ini.Contains("recommendation=", StringComparison.Ordinal) && ini.Contains("价格:IQ:速度权重", StringComparison.Ordinal),
        "Taskbar status must publish the weighted recommendation and its weights.");
    Check(ini.Contains("compact=! A100l S-- L", StringComparison.Ordinal),
        "Compact taskbar display must retain model scores, selected effort, and stale state.");
    Check(ini.Contains("档位 l=low", StringComparison.Ordinal),
        "The tooltip must explain compact effort abbreviations.");
}
finally { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true); }

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    var service = new RadarService();
    var live = await Task.WhenAll(RadarBenchmarks.All.Select(benchmark => service.FetchAsync(benchmark)));
    var deep = live.Single(snapshot => snapshot.Benchmark == RadarBenchmarks.DeepSwe);
    Check(deep.Models.Any(model => model.Id == "gpt-6-astra"), "Live DeepSWE data should include GPT-6 Astra.");
    Check(deep.Models.Any(model => model.Id == "gpt-6-sol"), "Live DeepSWE data should include GPT-6 Sol.");
    Check(deep.Models.Any(model => model.Id == "gpt-6-luna"), "Live DeepSWE data should include GPT-6 Luna.");
    Check(live.Single(snapshot => snapshot.Benchmark == RadarBenchmarks.Pompeii).Models.Count > 0, "Live visual-reasoning data should include GPT models.");
    foreach (var snapshot in live)
        Console.WriteLine($"LIVE PASS {snapshot.Benchmark}: {snapshot.Models.Count} GPT models; source {snapshot.SourceUpdatedAt}; GPT-6 scores {string.Join(", ", snapshot.Models.Where(model => model.Id.StartsWith("gpt-6-", StringComparison.Ordinal)).Select(model => $"{model.Id}={model.Score?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "暂无"} (n={model.Total ?? 0})"))}");
}

Console.WriteLine("PASS aggregate IQ, effort ordering, filtering, zero samples, mode/benchmark guard, stale cache, atomic persistence, and taskbar tooltip content.");
