using System.Text;
using System.Text.RegularExpressions;

namespace VpnManager.Core;

public sealed record DisplaySettings(string FontName, int FontSize, string Color, string Alignment)
{
    public static DisplaySettings Default => new("Microsoft YaHei UI", 13, "#1E77CF", "left");
    private static string PathFor(string directory) => Path.Combine(directory, "vpn-display-settings.ini");
    public static DisplaySettings Read(string directory)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadAllLines(PathFor(directory)))
            {
                var index = line.IndexOf('=');
                if (index > 0) pairs[line[..index].Trim()] = line[(index + 1)..].Trim();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var font = pairs.GetValueOrDefault("font_name", Default.FontName);
        var color = pairs.GetValueOrDefault("color", Default.Color);
        var alignment = pairs.GetValueOrDefault("alignment", "left");
        return new(string.IsNullOrWhiteSpace(font) ? Default.FontName : font,
            int.TryParse(pairs.GetValueOrDefault("font_size"), out var size) ? Math.Clamp(size, 8, 28) : Default.FontSize,
            Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$") ? color : Default.Color,
            alignment is "left" or "right" or "center" ? alignment : "left");
    }
    public static void Write(string directory, DisplaySettings value)
    {
        if (value.FontName.IndexOfAny(['\r', '\n', '=']) >= 0) throw new ArgumentException("字体名称不能包含换行或等号。");
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(PathFor(directory), $"[display]\r\nfont_name={value.FontName}\r\nfont_size={value.FontSize}\r\ncolor={value.Color}\r\nalignment={value.Alignment}\r\n", Encoding.Unicode);
    }
}
