using System.Text;

namespace VpnManager.Core;

/// <summary>
/// 落盘的操作日志。
///
/// 之所以需要它：原来所有操作记录只写在界面上的一个 TextBox 里，程序一关就没了，
/// 出问题时无法回溯"点了哪个按钮、卡在哪一步、等了多久"。日志写到
/// &lt;StateDirectory&gt;\logs\operations.log，超过大小上限自动轮转为 .1。
/// </summary>
public static class OperationLog
{
    private const long MaxBytes = 1L * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;
    private static string? _error;

    /// <summary>日志文件路径；未初始化时为 null。</summary>
    public static string? Path => _path;

    /// <summary>初始化日志目录。失败不会抛出，只记住错误原因，避免影响主流程。</summary>
    public static void Configure(string stateDirectory)
    {
        try
        {
            var directory = System.IO.Path.Combine(stateDirectory, "logs");
            Directory.CreateDirectory(directory);
            _path = System.IO.Path.Combine(directory, "operations.log");
            _error = null;
            Write($"===== 日志开始 pid={Environment.ProcessId} 管理员={IsElevated()} =====");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _path = null;
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_path is null) return;
            try
            {
                RotateIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                File.AppendAllText(_path, line, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>把一段可能多行的文本按行写入。</summary>
    public static void WriteBlock(string title, string? text)
    {
        Write($"--- {title} ---");
        if (string.IsNullOrWhiteSpace(text)) { Write("  (空)"); return; }
        foreach (var line in text.Replace("\r\n", "\n").Split('\n')) Write("  " + line);
        Write($"--- {title} 结束 ---");
    }

    public static string? LastError => _error;

    private static void RotateIfNeeded()
    {
        if (_path is null) return;
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes) return;
        var rolled = _path + ".1";
        try { if (File.Exists(rolled)) File.Delete(rolled); File.Move(_path, rolled); } catch { }
    }

    private static string IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "是" : "否";
        }
        catch { return "未知"; }
    }
}
