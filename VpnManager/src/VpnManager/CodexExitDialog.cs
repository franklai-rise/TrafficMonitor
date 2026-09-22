using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace VpnManager;

public sealed class CodexExitDialog : Window
{
    public CodexExitDialog(string target)
    {
        Title = "退出 Codex 后切换";
        Width = 500; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(244, 247, 251));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Codex 尚未完全退出", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = $"是否帮你退出 Codex，然后切换到 {target}？\n\n正在执行的 Codex 任务会被中断，请先保存未发送的内容。管理器会先请求正常退出；等待 15 秒后，再结束已核实的 Codex 后台进程。\n\n确认完全退出后才切换；退出失败则保留当前连接。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 22), LineHeight = 23
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, IsDefault = true, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 10, 0) };
        var confirm = new Button { Content = "退出 Codex 并切换", Padding = new Thickness(16, 9, 16, 9) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(cancel, "CancelCodexExit");
        System.Windows.Automation.AutomationProperties.SetAutomationId(confirm, "ConfirmCodexExit");
        cancel.Click += (_, _) => DialogResult = false;
        confirm.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(confirm); panel.Children.Add(actions); Content = panel;
        Loaded += (_, _) => cancel.Focus();
    }
}
