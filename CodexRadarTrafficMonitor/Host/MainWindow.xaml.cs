using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CodexRadarHost;

public partial class MainWindow : Window
{
    private readonly RadarCoordinator _coordinator;
    private bool _selectingModel;
    private string _selectedModelId = "gpt-6-astra";
    private string _benchmark = RadarBenchmarks.DeepSwe;
    private readonly ObservableCollection<RadarModelRow> _modelRows = [];
    private readonly ObservableCollection<RadarEffortRow> _effortRows = [];
    private readonly ObservableCollection<RankedEffortRow> _rankingRows = [];
    private RankingWeights _weights;

    public MainWindow(RadarCoordinator coordinator)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _weights = RankingWeightsStore.Load();
        PriceWeight.Value = _weights.Price;
        IqWeight.Value = _weights.Iq;
        SpeedWeight.Value = _weights.Speed;
        PriceWeight.ValueChanged += Weight_ValueChanged;
        IqWeight.ValueChanged += Weight_ValueChanged;
        SpeedWeight.ValueChanged += Weight_ValueChanged;
        BenchmarkTabs.SelectionChanged += BenchmarkTabs_SelectionChanged;
        ModelsGrid.ItemsSource = _modelRows;
        EffortsGrid.ItemsSource = _effortRows;
        RankingGrid.ItemsSource = _rankingRows;
        _coordinator.SnapshotChanged += Coordinator_SnapshotChanged;
        Closed += (_, _) => _coordinator.SnapshotChanged -= Coordinator_SnapshotChanged;
        Closing += Window_Closing;
        Render();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private bool _allowClose;
    public void BeginShutdown() => _allowClose = true;

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在读取两个评测频道的公开数据…";
        try { await _coordinator.RefreshNowAsync(); }
        finally { RefreshButton.IsEnabled = true; Render(); }
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://deng.codexradar.com/") { UseShellExecute = true });

    private void BenchmarkTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BenchmarkTabs.SelectedItem is not TabItem { Tag: string benchmark }) return;
        _benchmark = benchmark;
        Render();
    }

    private void ModelsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingModel || ModelsGrid.SelectedItem is not RadarModelRow row) return;
        _selectedModelId = row.ModelId;
        RenderEfforts(row);
    }

    private void RankingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RankingGrid.SelectedItem is not RankedEffortRow ranked) return;
        var model = _modelRows.FirstOrDefault(row => row.ModelId == ranked.ModelId);
        if (model is null) return;
        _selectedModelId = ranked.ModelId;
        ModelsGrid.SelectedItem = model;
        ModelsGrid.ScrollIntoView(model);
        RenderEfforts(model);
        EffortsGrid.SelectedItem = _effortRows.FirstOrDefault(row => row.Effort == ranked.Effort);
        if (EffortsGrid.SelectedItem is not null) EffortsGrid.ScrollIntoView(EffortsGrid.SelectedItem);
    }

    private void Weight_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var candidate = new RankingWeights((int)PriceWeight.Value, (int)IqWeight.Value, (int)SpeedWeight.Value);
        if (!candidate.IsValid) return;
        _weights = candidate;
        try { RankingWeightsStore.Save(candidate); }
        catch (Exception ex) { RadarLog.Write("保存综合权重失败", ex); }
        try { _coordinator.UpdateTaskbarStatus(); }
        catch (Exception ex) { RadarLog.Write("更新任务栏综合推荐失败", ex); }
        RenderRanking();
    }

    private void Coordinator_SnapshotChanged() => Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Render); return; }
        var cache = _coordinator.Current;
        cache.Benchmarks.TryGetValue(_benchmark, out var snapshot);
        RenderRanking(snapshot);
        _modelRows.Clear();
        if (snapshot is not null)
        {
            foreach (var model in snapshot.Models.OrderByDescending(item => item.Score ?? -1).ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
            {
                _modelRows.Add(new RadarModelRow
                {
                    ModelId = model.Id,
                    Model = PrettyModel(model.Id),
                    Score = model.Score,
                    Passed = model.Passed,
                    Total = model.Total,
                    Updated = FormatLocal(model.SourceUpdatedAt),
                    Efforts = model.Efforts.Select(ToRow).ToList()
                });
            }
        }

        var fetched = snapshot?.FetchedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN")) ?? "暂无成功读取记录";
        var source = FormatLocal(snapshot?.SourceUpdatedAt);
        if (snapshot is null)
            StatusText.Text = "正在首次读取公开数据；缓存会在读取完成后显示。";
        else if (snapshot.Stale)
            StatusText.Text = $"保留上次数据 · 本机读取：{fetched} · 来源更新：{source} · 更新失败：{snapshot.RefreshError ?? "网络连接失败"}，后台每 60 秒重试。";
        else
            StatusText.Text = $"已缓存 · 本机读取：{fetched} · 来源更新：{source} · 评分口径：{snapshot.ScoringMode} · 后台每 60 秒更新。";

        var target = _modelRows.FirstOrDefault(row => row.ModelId.Equals(_selectedModelId, StringComparison.OrdinalIgnoreCase))
            ?? _modelRows.FirstOrDefault();
        if (target is null)
        {
            _effortRows.Clear();
            SelectedModelText.Text = "暂无有效 GPT 模型成绩";
            return;
        }
        _selectingModel = true;
        ModelsGrid.SelectedItem = target;
        ModelsGrid.ScrollIntoView(target);
        _selectingModel = false;
        _selectedModelId = target.ModelId;
        RenderEfforts(target);
    }

    private void RenderRanking(BenchmarkSnapshot? snapshot = null)
    {
        if (snapshot is null) _coordinator.Current.Benchmarks.TryGetValue(_benchmark, out snapshot);
        PriceWeightText.Text = _weights.Price.ToString(CultureInfo.InvariantCulture);
        IqWeightText.Text = _weights.Iq.ToString(CultureInfo.InvariantCulture);
        SpeedWeightText.Text = _weights.Speed.ToString(CultureInfo.InvariantCulture);
        _rankingRows.Clear();
        foreach (var row in WeightedRanking.Calculate(snapshot, _weights))
            _rankingRows.Add(row);
    }

    private void RenderEfforts(RadarModelRow row)
    {
        _effortRows.Clear();
        foreach (var effort in row.Efforts) _effortRows.Add(effort);
        SelectedModelText.Text = $"{row.Model} · 汇总 IQ {row.ScoreText} · 样本 {row.PassedText} / {row.TotalText}";
    }

    private static RadarEffortRow ToRow(RadarEffort effort) => new()
    {
        Effort = effort.Effort,
        Score = effort.Score,
        Passed = effort.Passed,
        Total = effort.Total,
        RunsTotal = effort.RunsTotal,
        Runs24H = effort.Runs24H,
        Runs48H = effort.Runs48H,
        CostUsd = effort.CostUsd,
        Minutes = effort.Minutes,
        AgentSteps = effort.AgentSteps,
        TotalTokens = effort.TotalTokens,
        CacheHitRate = effort.CacheHitRate,
        SourceUpdatedAt = FormatLocal(effort.SourceUpdatedAt)
    };

    private static string PrettyModel(string id) => id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
        ? "GPT-" + string.Join(" ", id[4..].Split('-').Select(piece => piece.Length == 1
            ? piece.ToUpperInvariant()
            : char.ToUpperInvariant(piece[0]) + piece[1..]))
        : id;
    private static string FormatLocal(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
        ? time.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN")) : "暂无";
}
