using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dahu.Core;

public sealed class AppConfig
{
    public List<string> Brokers { get; set; } = new();

    public List<string> Servers { get; set; } = new();

    public List<string> ExcludedServers { get; set; } = new();

    public List<string> DomainFilter { get; set; } = new();

    public List<string> UpdShares { get; set; } = new();

    public List<string> FsLogixShares { get; set; } = new();

    public int RefreshSeconds { get; set; } = 60;
    public bool AutoRefresh { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 8;
    public bool ShowSystemSessions { get; set; } = false;
    public bool CollectPerformance { get; set; } = true;
    public bool ShadowConsentPrompt { get; set; } = false;
    public bool ShadowControl { get; set; } = true;

    public string Language { get; set; } = Loc.SystemDefault;

    public string Theme { get; set; } = Core.Theme.Light;

    public bool UseAltAccount { get; set; } = false;

    public string AltAccountUser { get; set; } = "";

    public bool AltAccountNetOnly { get; set; } = true;

    public string AltAccountPassword { get; set; } = "";

    public List<string> SessionColumns { get; set; } = new(DefaultSessionColumns);

    public static readonly string[] AllSessionColumns =
    {
        "Server", "SessionId", "User", "Domain", "State", "Client", "ClientIp", "Collection", "Type",
        "Logon", "Connect", "Disconnect", "Idle", "Resolution", "Protocol", "ClientBuild", "Station",
    };

    public static readonly string[] DefaultSessionColumns =
    {
        "Server", "SessionId", "User", "Domain", "State", "Client", "ClientIp", "Collection",
        "Logon", "Connect", "Disconnect", "ClientBuild", "Station",
    };

    public bool ConfirmActions { get; set; } = true;
    public int HistoryDays { get; set; } = 7;

    public string LdapPath { get; set; } = "";

    public string IgnoredUpdateVersion { get; set; } = "";

    public Dictionary<string, List<ColumnLayout>> ColumnLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 780;
    public bool WindowMaximized { get; set; } = false;

    [JsonIgnore]
    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 2, 120));

    public static List<string> SplitList(string s) =>
        s.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string JoinList(IEnumerable<string> l) => string.Join(Environment.NewLine, l);
}

public sealed class ColumnLayout
{
    public int Index { get; set; }
    public double Width { get; set; }
    public bool Star { get; set; }
    public int DisplayIndex { get; set; }
}

public static class ConfigService
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string FilePath { get; private set; } = Path.Combine(AppPaths.DataDir, "Dahu.config.json");

    public static AppConfig Current { get; private set; } = new();

    public static AppConfig Load(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("/config", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
                FilePath = Path.GetFullPath(args[i + 1]);
        }

        try
        {
            if (File.Exists(FilePath))
            {
                Current = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Opts) ?? new AppConfig();
                Log.Info($"Configuration chargée : {FilePath}");
            }
            else
            {
                Current = new AppConfig();
                Log.Info($"Aucune configuration, valeurs par défaut ({FilePath})");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Configuration illisible ({FilePath}) : copie en .bad et réinitialisation", ex);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            Current = new AppConfig();
        }
        return Current;
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Opts));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak");
        else File.Move(tmp, FilePath);
        Log.Info("Configuration sauvegardée");
    }

    public static void Export(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Current, Opts));

    public static void Import(string path)
    {
        Current = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Opts)
                  ?? throw new InvalidDataException("Fichier vide");
        Save();
    }
}
