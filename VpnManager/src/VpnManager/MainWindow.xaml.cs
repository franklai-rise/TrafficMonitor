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
    private readonly WindowsSystemGateway _system = new(); private readonly VpnPaths _paths = VpnPaths.Default;
    private readonly StatusCollector _collector; private readonly SnapshotStore _store; private readonly SwitchService _switcher; private readonly ClashControllerResolver _clashResolver; private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Forms.NotifyIcon _tray; private bool _switching; private DateTimeOffset _lastExitIpRefresh = DateTimeOffset.MinValue; private string? _exitIp; private string? _exitCountry; private string? _exitLocation;
    public MainWindow()
    {
        InitializeComponent(); _collector = new(_system, _paths); _store = new(_paths.StateDirectory); _switcher = new(_system, _paths, _collector, _store); _clashResolver = new(_paths.ClashConfig);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "VpnManager.ico");
        _tray = new Forms.NotifyIcon { Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Information, Text = "VPN 管理器", Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
        _tray.ContextMenuStrip.Items.Add("显示管理器", null, (_, _) => Dispatcher.Invoke(ShowFromTray)); _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() => { _tray.Visible = false; System.Windows.Application.Current.Shutdown(); })); _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _timer.Tick += async (_, _) => await RefreshStateAsync(); Loaded += async (_, _) => { await RefreshStateAsync(); _timer.Start(); };
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private async Task RefreshStateAsync()
    {
        if (_switching) return;
        await RefreshExitIpIfEnabledAsync();
        var seed = _collector.Collect(_exitIp, _exitCountry, _exitLocation); var node = seed.Mode == VpnMode.Clash ? await _clashResolver.TryResolveAsync(CancellationToken.None) : (null, null);
        var state = _collector.Collect(_exitIp, _exitCountry, _exitLocation, node.Item1, node.Item2); var snapshot = _collector.ToSnapshot(state); _store.Write(snapshot);
        StatusText.Text = snapshot.DisplayText; StatusDetail.Text = snapshot.Tooltip; _tray.Text = snapshot.DisplayText.Length > 63 ? snapshot.DisplayText[..63] : snapshot.DisplayText;
    }
    private async void SwitchClash_Click(object sender, RoutedEventArgs e) => await SwitchAsync(VpnMode.Clash);
    private async void SwitchTiziGo_Click(object sender, RoutedEventArgs e) => await SwitchAsync(VpnMode.TiziGo);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStateAsync();
    private async Task RefreshExitIpIfEnabledAsync()
    {
        if (!ExitIpEnabled.IsChecked.GetValueOrDefault() || DateTimeOffset.UtcNow - _lastExitIpRefresh < TimeSpan.FromSeconds(60) || _switching) return;
        _lastExitIpRefresh = DateTimeOffset.UtcNow;
        var state = _collector.Collect();
        if (state.Mode is not (VpnMode.Clash or VpnMode.TiziGo)) return;
        try
        {
            using var handler = new HttpClientHandler();
            if (state.Mode == VpnMode.Clash) { handler.UseProxy = true; handler.Proxy = new WebProxy($"http://127.0.0.1:{VpnPaths.ClashPort}"); }
            else handler.UseProxy = false;
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var geo = Ping0GeoParser.Parse(await client.GetStringAsync("https://ping0.cc/geo"));
            if (geo is null) { Append("ping0.cc 返回格式无效，保留旧值。"); return; }
            _exitIp = geo.Ip; _exitCountry = geo.Country; _exitLocation = geo.Location;
        }
        catch { Append("出口 IP 和地区刷新失败，保留旧值。"); }
    }
    private async Task SwitchAsync(VpnMode mode)
    {
        if (_switching) return; _switching = true; ClashButton.IsEnabled = TiziGoButton.IsEnabled = false;
        try { Append($"请求切换到 {mode}…"); var result = await _switcher.SwitchAsync(mode, CancellationToken.None); Append(result.Summary); StatusText.Text = result.FinalState.Mode.ToString(); StatusDetail.Text = _collector.ToSnapshot(result.FinalState).Tooltip; }
        catch (Exception ex) { Append($"未执行切换：{ex.Message}"); }
        finally { _switching = false; ClashButton.IsEnabled = TiziGoButton.IsEnabled = true; await RefreshStateAsync(); }
    }
    private void Append(string text) => LogText.AppendText($"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}");
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
    protected override void OnClosed(EventArgs e) { _timer.Stop(); _tray.Visible = false; _tray.Dispose(); base.OnClosed(e); }
}
