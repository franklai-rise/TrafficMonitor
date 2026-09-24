using System.Windows;
using System.Windows.Threading;
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
    private readonly CodexProxyService _proxy;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Forms.NotifyIcon _tray;
    private bool _applyingProxy, _initialized, _exiting, _exitAfterProxyApply;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"VPN 管理器 · {App.BuildVersion}";
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        MinHeight = Math.Min(MinHeight, Height);
        _collector = new(_system, _paths); _store = new(_paths.StateDirectory);
        _proxy = new(_system, _collector, _paths);
        OperationLog.Write($"启动：版本={App.BuildVersion}；仅监控与 Codex 代理设置；程序目录={AppContext.BaseDirectory}");
        var iconPath = Path.Combine(AppContext.BaseDirectory, "VpnManager.ico");
        _tray = new Forms.NotifyIcon { Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Information, Text = "VPN 管理器", Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
        _tray.ContextMenuStrip.Items.Add("显示管理器", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _tray.ContextMenuStrip.Items.Add("退出管理器", null, (_, _) => Dispatcher.Invoke(RequestExit));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var preference = Path.Combine(_paths.StateDirectory, "exit-ip-enabled.txt");
        try { if (File.Exists(preference)) ExitIpEnabled.IsChecked = File.ReadAllText(preference).Trim() == "true"; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        ExitIpEnabled.Checked += SaveExitPreference;
        ExitIpEnabled.Unchecked += SaveExitPreference;
        _timer.Tick += async (_, _) => await RefreshStateAsync();
        Loaded += InitializeOnce;
    }
    private async void InitializeOnce(object sender, RoutedEventArgs e)
    {
        // Loaded can fire again after a hidden window is shown.
        if (_initialized) return;
        _initialized = true;
        _timer.Start();
        await RefreshStateAsync();
    }
    private void SaveExitPreference(object sender, RoutedEventArgs e)
    {
        try { AtomicFile.WriteAllText(Path.Combine(_paths.StateDirectory, "exit-ip-enabled.txt"), ExitIpEnabled.IsChecked == true ? "true" : "false"); }
        catch (Exception ex) { Append($"设置保存失败：{ex.Message}"); }
        SignalHostRefresh();
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    public void ShowFromActivationRequest() => ShowFromTray();
    public void RequestExit()
    {
        if (_exiting) return;
        if (_applyingProxy)
        {
            _exitAfterProxyApply = true;
            Append("代理变量写入即将完成；完成复核后退出管理器。");
            return;
        }
        _exiting = true; _timer.Stop();
        _tray.Visible = false; System.Windows.Application.Current.Shutdown();
    }
    private static bool SignalHostRefresh()
    {
        try { using var signal = EventWaitHandle.OpenExisting("Local\\VpnStatusPlugin.Refresh"); return signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private async Task<bool> RefreshStateAsync(bool waitForCollection = false)
    {
        if (_exiting || _applyingProxy) return false;
        if (waitForCollection) await _operationGate.WaitAsync();
        else if (!await _operationGate.WaitAsync(0)) return false;
        try
        {
            if (_exiting || _applyingProxy) return false;
            var snapshot = _store.Read();
            if (_exiting) return false;
            if (snapshot is null || !snapshot.IsFresh || DateTimeOffset.Now - snapshot.ObservedAt > TimeSpan.FromSeconds(10))
            {
                StatusText.Text = "VPN 状态过期";
                StatusDetail.Text = "TrafficMonitor 插件尚未更新状态。请检查 TrafficMonitor 是否运行。";
                _tray.Text = "VPN 状态过期";
                return false;
            }
            StatusText.Text = snapshot.DisplayText;
            StatusDetail.Text = snapshot.Tooltip.StartsWith(snapshot.DisplayText + "\n", StringComparison.Ordinal)
                ? snapshot.Tooltip[(snapshot.DisplayText.Length + 1)..] : snapshot.Tooltip;
            var trayText = snapshot.DisplayText.Replace('\n', ' ');
            _tray.Text = trayText.Length > 63 ? trayText[..63] : trayText;
        }
        catch (Exception ex)
        {
            StatusDetail.Text = $"读取插件状态失败：{ex.Message}";
            Append(StatusDetail.Text);
            return false;
        }
        finally { _operationGate.Release(); }
        return true;
    }
    private async void SwitchClash_Click(object sender, RoutedEventArgs e) => await ApplyProxyAsync(VpnMode.Clash);
    private async void SwitchTiziGo_Click(object sender, RoutedEventArgs e) => await ApplyProxyAsync(VpnMode.TiziGo);
    private async void Direct_Click(object sender, RoutedEventArgs e) => await ApplyProxyAsync(VpnMode.Direct);
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false; RefreshButton.Content = "正在刷新…";
        SetActionSummary($"{DateTime.Now:HH:mm:ss} 正在刷新本机状态与出口地区…", "#1D4ED8");
        Append("正在刷新本机状态与出口地区…");
        try
        {
            if (!SignalHostRefresh()) { Append("TrafficMonitor 状态组件未运行。"); SetActionSummary("TrafficMonitor 状态组件未运行，请先启动 TrafficMonitor。", "#9A6700"); return; }
            await Task.Delay(1200);
            await RefreshStateAsync(true);
            Append($"本机状态已刷新：{DateTime.Now:HH:mm:ss}；出口结果及采集时间见状态详情。");
            SetActionSummary($"{DateTime.Now:HH:mm:ss} 本机状态已刷新。\n出口信息以状态详情中的采集时间为准。", "#166534");
        }
        catch (Exception ex) { Append($"刷新失败：{ex.Message}"); SetActionSummary($"刷新失败：{ex.Message}", "#B42318"); }
        finally { RefreshButton.Content = "刷新状态"; RefreshButton.IsEnabled = !_applyingProxy; }
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

    private async Task ApplyProxyAsync(VpnMode mode)
    {
        if (_applyingProxy || _exiting) return;
        _applyingProxy = true;
        ClashButton.IsEnabled = TiziGoButton.IsEnabled = DirectButton.IsEnabled = RefreshButton.IsEnabled = false;
        var label = mode switch { VpnMode.Clash => "Clash 7890", VpnMode.TiziGo => "TiziGo TUN", _ => "普通直连" };
        SetActionSummary($"{DateTime.Now:HH:mm:ss} 正在核实 {label} 并同步 Codex 代理…", "#1D4ED8");
        Append($"请求同步 Codex 代理：{label}。VPN 软件保持由你手动控制。");
        var enteredGate = false;
        try
        {
            await _operationGate.WaitAsync();
            enteredGate = true;
            var result = await Task.Run(() => _proxy.Apply(mode));
            Append(result.Summary);
            SetActionSummary($"{DateTime.Now:HH:mm:ss} {result.Summary}", result.Success ? "#166534" : "#B42318");
        }
        catch (Exception ex)
        {
            var message = $"Codex 代理设置未完成，请核对变量：{ex.Message}";
            Append(message); SetActionSummary(message, "#B42318");
        }
        finally
        {
            if (enteredGate) _operationGate.Release();
            _applyingProxy = false;
            ClashButton.IsEnabled = TiziGoButton.IsEnabled = DirectButton.IsEnabled = RefreshButton.IsEnabled = true;
            if (_exitAfterProxyApply) RequestExit();
            else if (!_exiting) await RefreshStateAsync();
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
    { _exiting = true; _timer.Stop(); _tray.Visible = false; _tray.Dispose(); OperationLog.Write("程序退出"); base.OnClosed(e); }
}
