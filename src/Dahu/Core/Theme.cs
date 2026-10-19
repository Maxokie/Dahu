using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Dahu.Core;

public static class Theme
{
    public const string Light = "light";
    public const string Dark = "dark";
    private const string Folder = "pack://application:,,,/Views/Themes/";
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint SWP_FRAMECHANGED = 0x0020, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    public static string Current { get; private set; } = Light;

    public static bool IsDark => Current == Dark;

    public static string Normalize(string? theme)
        => string.Equals(theme?.Trim(), Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;

    public static void Apply(string? theme)
    {
        theme = Normalize(theme);
        var app = Application.Current;
        if (app == null) return;
        var dicts = app.Resources.MergedDictionaries;
        var dict = new ResourceDictionary { Source = new Uri(Folder + (theme == Dark ? "Dark.xaml" : "Light.xaml")) };
        var index = -1;
        for (int i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("/Themes/", StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        }
        if (index >= 0) dicts[index] = dict;
        else dicts.Insert(0, dict);
        Current = theme;
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Log.Info($"Thème appliqué : {theme}");
    }

    public static void Attach(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyTitleBar(window);
        else window.SourceInitialized += (_, _) => ApplyTitleBar(window);
    }

    private static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
