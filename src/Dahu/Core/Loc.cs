using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Dahu.Core;

public sealed record LangInfo(string Code, string Name);

public sealed class Loc : INotifyPropertyChanged
{
    public const string Fallback = "en";
    public const string FolderName = "lang";
    private const string ResourcePrefix = "Dahu.Lang.";
    private const string NameKey = "Lang.Name";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Tables = LoadTables();

    public static IReadOnlyList<LangInfo> Languages { get; } = Tables
        .Select(t => new LangInfo(t.Key, t.Value.TryGetValue(NameKey, out var n) && n.Length > 0 ? n : t.Key))
        .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public static Loc I { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string SystemDefault
    {
        get
        {
            var ui = CultureInfo.CurrentUICulture;
            var full = Normalize(ui.Name, "");
            return full.Length > 0 ? full : Normalize(ui.TwoLetterISOLanguageName);
        }
    }

    public static string Normalize(string? lang, string fallback = Fallback)
    {
        if (string.IsNullOrWhiteSpace(lang)) return fallback;
        lang = lang.Trim().ToLowerInvariant();
        return Tables.ContainsKey(lang) ? lang : fallback;
    }

    public static CultureInfo Culture(string lang)
    {
        try { return CultureInfo.CreateSpecificCulture(lang); }
        catch { return CultureInfo.GetCultureInfo("en-US"); }
    }

    private string _lang = SystemDefault;
    public string Language
    {
        get => _lang;
        set
        {
            _lang = Normalize(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public string this[string key]
    {
        get
        {
            if (Tables.TryGetValue(_lang, out var table) && table.TryGetValue(key, out var v)) return v;
            return Tables.TryGetValue(Fallback, out var fb) && fb.TryGetValue(key, out var f) ? f : key;
        }
    }

    public static string T(string key) => I[key];

    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase) || !res.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            var code = res[ResourcePrefix.Length..^".json".Length];
            using var stream = asm.GetManifestResourceStream(res);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            Merge(tables, code, reader.ReadToEnd(), res);
        }

        foreach (var dir in new[] { Path.Combine(AppPaths.ExeDir, FolderName), Path.Combine(AppPaths.DataDir, FolderName) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                    Merge(tables, Path.GetFileNameWithoutExtension(file), File.ReadAllText(file), file);
            }
            catch (Exception ex) { Log.Error($"Lecture du dossier de langues {dir}", ex); }
        }
        return tables;
    }

    private static void Merge(Dictionary<string, Dictionary<string, string>> tables, string code, string json, string source)
    {
        try
        {
            code = code.Trim().ToLowerInvariant();
            if (code.Length == 0) return;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts);
            if (entries == null) return;
            if (!tables.TryGetValue(code, out var table)) tables[code] = table = new Dictionary<string, string>();
            foreach (var (k, v) in entries) if (v != null) table[k] = v;
        }
        catch (Exception ex) { Log.Error($"Fichier de langue illisible : {source}", ex); }
    }
}
