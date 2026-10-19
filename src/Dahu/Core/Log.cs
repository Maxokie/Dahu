using System.IO;
using System.Text;

namespace Dahu.Core;

public static class Log
{
    private static readonly object _lock = new();
    private static string? _path;
    private static StreamWriter? _writer;
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string Path => _path ??= System.IO.Path.Combine(AppPaths.DataDir, "Dahu.log");

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg, Exception? ex = null)
        => Write("ERROR", ex == null ? msg : $"{msg} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (_lock)
            {
                if (_writer != null && _writer.BaseStream.Length > MaxBytes)
                {
                    _writer.Dispose();
                    _writer = null;
                }
                if (_writer == null)
                {
                    Rotate();
                    _writer = Open();
                }
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId,3}] {msg}");
            }
        }
        catch
        {
        }
    }

    private static void Rotate()
    {
        var fi = new FileInfo(Path);
        if (!fi.Exists || fi.Length <= MaxBytes) return;
        var old = Path + ".old";
        File.Delete(old);
        File.Move(Path, old);
    }

    private static StreamWriter Open()
    {
        var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }
}

public static class AppPaths
{
    private static string? _dataDir;

    public static string ExeDir
    {
        get
        {
            var exe = Environment.ProcessPath;
            var dir = string.IsNullOrEmpty(exe) ? null : System.IO.Path.GetDirectoryName(exe);
            return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
        }
    }

    public static bool IsPortable { get; private set; }

    public static string DataDir
    {
        get
        {
            if (_dataDir != null) return _dataDir;

            var portable = System.IO.Path.Combine(ExeDir, "Dahu.data");
            if (Directory.Exists(portable) && IsWritable(portable))
            {
                _dataDir = portable;
                IsPortable = true;
            }
            else
            {
                var appdata = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dahu");
                Directory.CreateDirectory(appdata);
                _dataDir = appdata;
                IsPortable = false;
            }
            return _dataDir;
        }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = System.IO.Path.Combine(dir, ".dahu-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
