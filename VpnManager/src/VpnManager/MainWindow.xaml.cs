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
    private readonly StatusCollector _collector; private readonly SnapshotStore _store; private readonly SwitchService _switcher; private readonly ClashControllerResolver _clashResolver; private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Forms.NotifyIcon _tray; private bool _switching; private bool _refreshing; private DateTimeOffset _lastExitIpRefresh = DateTimeOffset.MinValue; private DateTimeOffset _lastNodeRefresh = DateTimeOffset.MinValue; private string? _exitIp; private string? _exitCountry; private string? _exitLocation; private string? _clashNode; private string? _clashCountry;
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
        if (_switching || _refreshing) return;
        _refreshing = true;
        try
        {
            await RefreshExitIpIfEnabledAsync();
            var seed = _collector.Collect(_exitIp, _exitCountry, _exitLocation);
            if (seed.Mode == VpnMode.Clash && DateTimeOffset.UtcNow - _lastNodeRefresh >= TimeSpan.FromSeconds(30)) { (_clashNode, _clashCountry) = await _clashResolver.TryResolveAsync(CancellationToken.None); _lastNodeRefresh = DateTimeOffset.UtcNow; }
            if (seed.Mode != VpnMode.Clash) { _clashNode = _clashCountry = null; _lastNodeRefresh = DateTimeOffset.MinValue; }
            var state = _collector.Collect(_exitIp, _exitCountry, _exitLocation, _clashNode, _clashCountry); var snapshot = _collector.ToSnapshot(state); _store.Write(snapshot);
            StatusText.Text = snapshot.DisplayText; StatusDetail.Text = snapshot.Tooltip; _tray.Text = snapshot.DisplayText.Length > 63 ? snapshot.DisplayText[..63] : snapshot.DisplayText;
        }
        finally { _refreshing = false; }
    }
    private async void SwitchClash_Click(object sender, RoutedEventArgs e) => await SwitchAsync(VpnMode.Clash);
    private async void SwitchTiziGo_Click(object sender, RoutedEventArgs e) => await SwitchAsync(VpnMode.TiziGo);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStateAsync();
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
        var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right }; var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 80, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => dialog.Close(); var save = new System.Windows.Controls.Button { Content = "保存", Width = 80, IsDefault = true }; save.Click += (_, _) => { if (!int.TryParse(size.Text, out var value) || value is < 8 or > 28 || !System.Text.RegularExpressions.Regex.IsMatch(color.Text, "^#[0-9A-Fa-f]{6}$")) { System.Windows.MessageBox.Show(dialog, "字号需为 8 到 28，颜色格式为 #RRGGBB。", "VPN 状态显示样式", MessageBoxButton.OK, MessageBoxImage.Warning); return; } DisplaySettings.Write(_paths.StateDirectory, new(font.Text.Trim() is { Length: > 0 } name ? name : "Microsoft YaHei UI", value, color.Text.ToUpperInvariant(), alignment.SelectedIndex switch { 1 => "center", 2 => "right", _ => "left" })); Append("已保存 VPN 状态的独立显示样式；TrafficMonitor 将在下一次刷新应用。"); dialog.Close(); }; actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions); dialog.Content = panel; dialog.ShowDialog();
    }
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
    private sealed record DisplaySettings(string FontName, int FontSize, string Color, string Alignment)
    {
        private static string PathFor(string directory) => Path.Combine(directory, "vpn-display-settings.ini");
        public static DisplaySettings Read(string directory)
        {
            if (!File.Exists(PathFor(directory))) return new("Microsoft YaHei UI", 13, "#1E77CF", "left");
            var pairs = File.ReadAllLines(PathFor(directory)).Where(x => x.Contains('=')).Select(x => x.Split('=', 2)).ToDictionary(x => x[0].Trim(), x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);
            return new(pairs.GetValueOrDefault("font_name", "Microsoft YaHei UI"), int.TryParse(pairs.GetValueOrDefault("font_size"), out var size) ? Math.Clamp(size, 8, 28) : 13, pairs.GetValueOrDefault("color", "#1E77CF"), pairs.GetValueOrDefault("alignment", "left"));
        }
        public static void Write(string directory, DisplaySettings value) => File.WriteAllText(PathFor(directory), $"[display]{Environment.NewLine}font_name={value.FontName}{Environment.NewLine}font_size={value.FontSize}{Environment.NewLine}color={value.Color}{Environment.NewLine}alignment={value.Alignment}{Environment.NewLine}");
    }
}
