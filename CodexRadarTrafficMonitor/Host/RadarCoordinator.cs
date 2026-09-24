using System.Threading;
using System.Net.Http;
using System.Windows;

namespace CodexRadarHost;

public sealed class RadarCoordinator : IDisposable
{
    private readonly RadarCacheStore _store;
    private readonly RadarService _service;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private RadarCache _cache;
    private Task? _refreshLoop;

    public event Action? SnapshotChanged;
    public RadarCache Current => _cache;

    public RadarCoordinator(RadarCacheStore store, RadarService service)
    {
        _store = store;
        _service = service;
        _cache = _store.Load();
        _store.WriteStatus(_cache);
    }

    public void Start()
    {
        if (_refreshLoop is not null) return;
        _refreshLoop = Task.Run(RefreshLoopAsync);
    }

    public void UpdateTaskbarStatus() => _store.WriteStatus(_cache);

    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tasks = RadarBenchmarks.All.Select(async benchmark =>
            {
                try { return (Benchmark: benchmark, Snapshot: await _service.FetchAsync(benchmark, cancellationToken).ConfigureAwait(false), Error: (Exception?)null); }
                catch (Exception ex) { return (Benchmark: benchmark, Snapshot: (BenchmarkSnapshot?)null, Error: ex); }
            });
            var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
            var updated = new RadarCache
            {
                SchemaVersion = _cache.SchemaVersion,
                SavedAtUtc = _cache.SavedAtUtc,
                Benchmarks = new Dictionary<string, BenchmarkSnapshot>(_cache.Benchmarks, StringComparer.Ordinal)
            };
            foreach (var outcome in outcomes)
            {
                RadarCachePolicy.Apply(updated, outcome.Benchmark, outcome.Snapshot, outcome.Error);
                if (outcome.Snapshot is not null)
                {
                    RadarLog.Write($"{outcome.Benchmark} 更新成功：{outcome.Snapshot.Models.Count} 个 GPT 模型");
                }
                else
                {
                    RadarLog.Write($"{outcome.Benchmark} 更新失败", outcome.Error);
                }
            }
            _cache = updated;
            try { _store.Save(updated); }
            catch (Exception ex) { RadarLog.Write("写入雷达缓存失败", ex); }
        }
        finally { _refreshGate.Release(); }
        SnapshotChanged?.Invoke();
    }

    private async Task RefreshLoopAsync()
    {
        await RefreshNowAsync(_cancellation.Token).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(_cancellation.Token).ConfigureAwait(false))
                await RefreshNowAsync(_cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RadarLog.Write("后台更新循环异常", ex); }
    }

    internal static string FriendlyError(Exception? error) => error switch
    {
        null => "未知错误",
        TaskCanceledException => "请求超时",
        HttpRequestException http when http.StatusCode.HasValue => $"雷达接口返回 HTTP {(int)http.StatusCode.Value}",
        HttpRequestException => "网络连接失败",
        _ => error.Message
    };

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _refreshLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cancellation.Dispose();
        _refreshGate.Dispose();
    }
}

public static class RadarCachePolicy
{
    public static void Apply(RadarCache cache, string benchmark, BenchmarkSnapshot? fresh, Exception? error)
    {
        if (fresh is not null)
        {
            fresh.Stale = false;
            fresh.RefreshError = null;
            cache.Benchmarks[benchmark] = fresh;
            return;
        }

        cache.Benchmarks.TryGetValue(benchmark, out var prior);
        cache.Benchmarks[benchmark] = new BenchmarkSnapshot
        {
            Benchmark = benchmark,
            BenchmarkTitle = prior?.BenchmarkTitle ?? RadarBenchmarks.Title(benchmark),
            ScoreLabel = prior?.ScoreLabel ?? "IQ",
            ScoringMode = prior?.ScoringMode ?? "equal_latest_3",
            SourceUpdatedAt = prior?.SourceUpdatedAt,
            FetchedAtUtc = prior?.FetchedAtUtc,
            Stale = true,
            RefreshError = RadarCoordinator.FriendlyError(error),
            Models = prior?.Models ?? []
        };
    }
}
