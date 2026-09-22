using System.Windows;
using System.Windows.Threading;
using System.Net;
using System.Net.Http;
using System.IO;
using VpnManager.Core;
using Forms = System.Windows.Forms;

namespace VpnManager;
public partial class MainWindow : Window
{
    private readonly WindowsSystemGateway _system = new();
    private readonly VpnPaths _paths = VpnPaths.Default;
    private readonly StatusCollector _collector;
    private readonly SnapshotStore _store;
    private readonly SwitchService _switcher;
    private readonly ClashControllerResolver _clashResolver;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Forms.NotifyIcon _tray;
    private readonly bool _startupDirect;
    private bool _switching, _initialized, _exiting;
    private Task? _detailsTask;
    private Task? _progressTask;
    private CancellationTokenSource? _detailsCancellation;
    private VpnMode _observedMode = VpnMode.Unknown;
    private long _generation;
    private DateTimeOffset _lastDetailsAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset? _exitCapturedAt;
    private string? _exitIp, _exitCountry, _exitLocation, _clashNode, _clashCountry, _lastError;

    public MainWindow(bool startupDirect = false)
    {
        _startupDirect = startupDirect;
        InitializeComponent();
        Title = $"VPN 管理器 · {App.BuildVersion}";
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        MinHeight = Math.Min(MinHeight, Height);
        _collector = new(_system, _paths); _store = new(_paths.StateDirectory);
        _switcher = new(_system, _paths, _collector, _store); _clashResolver = new(_paths.ClashConfig);
        OperationLog.Write($"启动：版本={App.BuildVersion}；startupDirect={startupDirect}；程序目录={AppContext.BaseDirectory}");
        var iconPath = Path.Combine(AppContext.BaseDirectory, "VpnManager.ico");
        _tray = new Forms.NotifyIcon { Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Information, Text = "VPN 管理器", Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
        _tray.ContextMenuStrip.Items.Add("显示管理器", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(RequestExit));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var preference = Path.Combine(_paths.StateDirectory, "exit-ip-enabled.txt");
        try { if (File.Exists(preference)) ExitIpEnabled.IsChecked = File.ReadAllText(preference).Trim() == "true"; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        ExitIpEnabled.Checked += SaveExitPreference;
        ExitIpEnabled.Unchecked += SaveExitPreference;
        _timer.Tick += async (_, _) => { if (_switching) await PublishSwitchProgressAsync(); else await RefreshStateAsync(); };
        Loaded += InitializeOnce;
    }
    private async void InitializeOnce(object sender, RoutedEventArgs e)
    {
        // Loaded can fire again after a hidden window is shown. Login action is once per process.
        if (_initialized) return;
        _initialized = true;
        _timer.Start();
        if (_startupDirect) { Hide(); await SwitchAsync(VpnMode.Direct); }
        else await RefreshStateAsync();
    }
    private Task PublishSwitchProgressAsync()
    {
        if (_progressTask is { IsCompleted: false }) return _progressTask;
        var snapshot = new StatusSnapshot(StatusSnapshot.CurrentSchema, "VPN 正在切换\n请等待完成", "管理器正在切换或恢复 VPN，出口探测已暂停。结果请查看管理器。", "Switching", "", "VPN", "未知", "操作进度", null, DateTimeOffset.Now, true, null);
        _progressTask = Task.Run(() => { try { _store.Write(snapshot); } catch (Exception ex) { OperationLog.Write($"切换进度写入失败：{ex.GetType().Name}"); } });
        return _progressTask;
    }
    private void SaveExitPreference(object sender, RoutedEventArgs e)
    {
        try { AtomicFile.WriteAllText(Path.Combine(_paths.StateDirectory, "exit-ip-enabled.txt"), ExitIpEnabled.IsChecked == true ? "true" : "false"); }
        catch (Exception ex) { Append($"设置保存失败：{ex.Message}"); }
        if (ExitIpEnabled.IsChecked != true) _detailsCancellation?.Cancel();
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    public void ShowFromActivationRequest() => ShowFromTray();
    public void RequestExit()
    {
        if (_switching) { ShowFromTray(); Append("切换尚未结束，请等待恢复或切换完成后退出。"); return; }
        _exiting = true; _timer.Stop(); _detailsCancellation?.Cancel();
        _tray.Visible = false; System.Windows.Application.Current.Shutdown();
    }
    private void ClearPathDetails()
    {
        _generation++;
        _detailsCancellation?.Cancel();
        _exitIp = _exitCountry = _exitLocation = _clashNode = _clashCountry = null;
        _exitCapturedAt = null; _lastDetailsAttempt = DateTimeOffset.MinValue;
    }
    private async Task<bool> RefreshStateAsync(bool waitForCollection = false)
    {
        if (_exiting || _switching) return false;
        if (waitForCollection) await _operationGate.WaitAsync();
        else if (!await _operationGate.WaitAsync(0)) return false;
        try
        {
            if (_exiting || _switching) return false;
            var state = await Task.Run(() => _collector.Collect());
            if (_lastError?.StartsWith("本机状态刷新失败：", StringComparison.Ordinal) == true) _lastError = null;
            if (state.Mode != _observedMode) { ClearPathDetails(); _observedMode = state.Mode; }
            state = state with { ExitIp = _exitIp, ExitCountry = _exitCountry, ExitLocation = _exitLocation, ClashNode = _clashNode, ClashCountry = _clashCountry };
            var snapshot = _collector.ToSnapshot(state, _lastError);
            if (_exitCapturedAt is { } captured)
            {
                var age = DateTimeOffset.Now - captured;
                snapshot = snapshot with { Tooltip = snapshot.Tooltip + $"\n出口采集：{captured:HH:mm:ss}（{(age.TotalSeconds > 90 ? "已过期，上次结果" : "仅代表该次探测出口")}）" };
            }
            await Task.Run(() => _store.Write(snapshot));
            if (_exiting) return false;
            StatusText.Text = snapshot.DisplayText;
            StatusDetail.Text = snapshot.Tooltip.StartsWith(snapshot.DisplayText + "\n", StringComparison.Ordinal)
                ? snapshot.Tooltip[(snapshot.DisplayText.Length + 1)..] : snapshot.Tooltip;
            var trayText = snapshot.DisplayText.Replace('\n', ' ');
            _tray.Text = trayText.Length > 63 ? trayText[..63] : trayText;
        }
        catch (Exception ex)
        {
            _lastError = $"本机状态刷新失败：{ex.Message}";
            StatusDetail.Text = _lastError;
            Append(_lastError);
            return false;
        }
        finally { _operationGate.Release(); }
        _ = RefreshDetailsAsync(false);
        return true;
    }
    private Task RefreshDetailsAsync(bool force)
    {
        if (_exiting || _switching || _observedMode is not (VpnMode.Clash or VpnMode.TiziGo or VpnMode.Direct)) return Task.CompletedTask;
        if (_detailsTask is { IsCompleted: false }) return _detailsTask;
        if (!force && DateTimeOffset.Now - _lastDetailsAttempt < TimeSpan.FromSeconds(60)) return Task.CompletedTask;
        _lastDetailsAttempt = DateTimeOffset.Now;
        _detailsCancellation?.Dispose();
        _detailsCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _detailsTask = ReadDetailsAsync(_observedMode, _generation, ExitIpEnabled.IsChecked == true, _detailsCancellation.Token);
        return _detailsTask;
    }
    private async Task ReadDetailsAsync(VpnMode mode, long generation, bool readExit, CancellationToken token)
    {
        try
        {
            if (mode == VpnMode.Clash)
            {
                var (node, country) = await _clashResolver.TryResolveAsync(token);
                if (generation != _generation || _switching || _exiting) return;
                if (node != _clashNode) { _exitIp = _exitCountry = _exitLocation = null; _exitCapturedAt = null; }
                (_clashNode, _clashCountry) = (node, country);
            }
            if (!readExit) return;
            using var handler = new HttpClientHandler { UseProxy = mode == VpnMode.Clash, Proxy = mode == VpnMode.Clash ? new WebProxy($"http://127.0.0.1:{VpnPaths.ClashPort}") : null };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var geo = Ping0GeoParser.Parse(await client.GetStringAsync("https://ping0.cc/geo", token));
            if (generation != _generation || _switching || _exiting) return;
            if (geo is null) throw new IOException("出口地区服务返回了无法识别的内容。");
            _exitIp = geo.Ip; _exitCountry = geo.Country; _exitLocation = geo.Location; _exitCapturedAt = DateTimeOffset.Now;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!_exiting && generation == _generation) Append($"出口地区更新失败，保留带时间的上次结果：{ex.Message}"); }
    }
    private async void SwitchClash_Click(object sender, RoutedEventArgs e) => await SwitchAsync(VpnMode.Clash);
    private async void SwitchTiziGo_Click(object sender, RoutedEventArgs e) => await SwitchAsync(VpnMode.TiziGo);
    private async void Direct_Click(object sender, RoutedEventArgs e) => await SwitchAsync(VpnMode.Direct);
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false; RefreshButton.Content = "正在刷新…";
        SetActionSummary($"{DateTime.Now:HH:mm:ss} 正在刷新本机状态与出口地区…", "#1D4ED8");
        Append("正在刷新本机状态与出口地区…");
        try
        {
            if (!await RefreshStateAsync(true)) { Append("本机采集尚未完成，稍后自动更新。"); SetActionSummary("本机采集尚未完成，请查看状态详情；稍后将自动更新。", "#9A6700"); return; }
            await RefreshDetailsAsync(true);
            await RefreshStateAsync(true);
            Append($"本机状态已刷新：{DateTime.Now:HH:mm:ss}；出口结果及采集时间见状态详情。");
            SetActionSummary($"{DateTime.Now:HH:mm:ss} 本机状态已刷新。\n出口信息以状态详情中的采集时间为准。", "#166534");
        }
        catch (Exception ex) { Append($"刷新失败：{ex.Message}"); SetActionSummary($"刷新失败：{ex.Message}", "#B42318"); }
        finally { RefreshButton.Content = "刷新状态"; RefreshButton.IsEnabled = !_switching; }
    }
    private void DisplayStyle_Click(object sender, RoutedEventArgs e)
    {
        var settings = DisplaySettings.Read(_paths.StateDirectory);
        var dialog = new Window { Title = "VPN 状态显示样式", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(22), MinWidth = 330 };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "这些设置只影响 TrafficMonitor 中的 VPN 状态。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "字体名称" }); var font = new System.Windows.Controls.TextBox { Text = settings.FontName, Margin = new Thickness(0, 4, 0, 10) }; panel.Children.Add(font);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "字号（8–28）" }); var size = new System.Windows.Controls.TextBox { Text = settings.FontSize.ToString(), Margin = new Thickness(0, 4, 0, 10) }; panel.Children.Add(size);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "文字颜色（#RRGGBB）" }); var color = new System.Windows.Controls.TextBox { Text = settings.Color, Margin = new Thickness(0, 4, 0, 10) }; panel.Children.Add(color);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "对齐" }); var alignment = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 4, 0, 16) }; alignment.Items.Add("左对齐"); alignment.Items.Add("居中"); alignment.Items.Add("右对齐"); alignment.SelectedIndex = settings.Alignment switch { "center" => 1, "right" => 2, _ => 0 }; panel.Children.Add(alignment);
        var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right }; var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 80, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => dialog.Close(); var save = new System.Windows.Controls.Button { Content = "保存", Width = 80, IsDefault = true }; save.Click += (_, _) => { if (!int.TryParse(size.Text, out var value) || value is < 8 or > 28 || !System.Text.RegularExpressions.Regex.IsMatch(color.Text, "^#[0-9A-Fa-f]{6}$")) { System.Windows.MessageBox.Show(dialog, "字号需为 8 到 28，颜色格式为 #RRGGBB。", "VPN 状态显示样式", MessageBoxButton.OK, MessageBoxImage.Warning); return; } try { DisplaySettings.Write(_paths.StateDirectory, new(font.Text.Trim() is { Length: > 0 } name ? name : "Microsoft YaHei UI", value, color.Text.ToUpperInvariant(), alignment.SelectedIndex switch { 1 => "center", 2 => "right", _ => "left" })); Append("已保存 VPN 状态的独立显示样式；TrafficMonitor 将在下一次刷新应用。"); dialog.Close(); } catch (Exception ex) { System.Windows.MessageBox.Show(dialog, $"保存失败：{ex.Message}", "VPN 状态显示样式"); } }; actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions); dialog.Content = panel; dialog.ShowDialog();
    }

    private async Task SwitchAsync(VpnMode mode)
    {
        if (_switching || _exiting) return;
        _switching = true; _detailsCancellation?.Cancel();
        ClashButton.IsEnabled = TiziGoButton.IsEnabled = DirectButton.IsEnabled = RefreshButton.IsEnabled = false;
        var label = mode == VpnMode.Direct ? "普通直连" : mode.ToString();
        var requestedAt = DateTime.Now;
        SetActionSummary($"{requestedAt:HH:mm:ss} 请求切换到 {label}…", "#1D4ED8");
        Append($"请求切换到 {label}…");
        await _operationGate.WaitAsync();
        try
        {
            if (_detailsTask is not null) await _detailsTask;
            ClearPathDetails();
            var result = await Task.Run(() => _switcher.SwitchAsync(mode, CancellationToken.None));
            _lastError = result.Success ? null : result.Summary;
            Append(result.Summary);
            SetActionSummary($"{requestedAt:HH:mm:ss} 请求切换到 {label}…\n{DateTime.Now:HH:mm:ss} {result.Summary}", result.Success ? "#166534" : "#B42318");
        }
        catch (Exception ex) { _lastError = $"操作未完成，请核实状态：{ex.Message}"; Append(_lastError); SetActionSummary(_lastError, "#B42318"); }
        finally
        {
            if (_progressTask is not null) await _progressTask;
            _operationGate.Release(); _switching = false;
            ClashButton.IsEnabled = TiziGoButton.IsEnabled = DirectButton.IsEnabled = RefreshButton.IsEnabled = true;
            await RefreshStateAsync();
        }
    }
    private void Append(string text)
    {
        if (LogText.Text.Length > 50000) LogText.Clear();
        LogText.AppendText($"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}");
        ScrollLogToLatest(); OperationLog.Write(text);
    }
    private void ScrollLogToLatest()
    {
        // Wait for wrapping and layout before calculating the final scroll offset.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_exiting) return;
            LogText.UpdateLayout();
            LogText.ScrollToEnd();
        }));
    }
    private void LogText_Loaded(object sender, RoutedEventArgs e) => ScrollLogToLatest();
    private void LatestLog_Click(object sender, RoutedEventArgs e) => ScrollLogToLatest();
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (OverviewScroll is null || LogText is null) return;
        OverviewScroll.MaxHeight = Math.Max(240, Math.Min(430, ActualHeight - 420));
        ScrollLogToLatest();
    }
    private void SetActionSummary(string text, string color)
    {
        ActionSummaryText.Text = text;
        ActionSummaryText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    { if (!_exiting) { e.Cancel = true; Hide(); } base.OnClosing(e); }
    protected override void OnClosed(EventArgs e)
    { _exiting = true; _timer.Stop(); _detailsCancellation?.Cancel(); _tray.Visible = false; _tray.Dispose(); OperationLog.Write("程序退出"); base.OnClosed(e); }
}
