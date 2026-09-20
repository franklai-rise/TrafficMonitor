using System.Text;

namespace VpnManager.Core;

public static class AtomicFile
{
    private static readonly object Gate = new();
    public static void WriteAllText(string path, string text, Encoding? encoding = null)
    {
        lock (Gate) WriteCore(path, text, encoding);
    }
    private static void WriteCore(string path, string text, Encoding? encoding)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, encoding ?? new UTF8Encoding(false));
            // A reader can temporarily hold the destination without delete sharing.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, true); break; }
                catch (Exception ex) when (attempt < 8 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(20); }
            }
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
