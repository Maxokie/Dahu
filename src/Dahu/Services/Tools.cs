using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Dahu.Core;
using Dahu.Models;

namespace Dahu.Services;

public static class Tools
{
    public static void Shadow(RdsSession s, bool control, bool consentPrompt)
    {
        var args = $"/v:{s.Server} /shadow:{s.SessionId}";
        if (control) args += " /control";
        if (!consentPrompt) args += " /noConsentPrompt";
        Start("mstsc.exe", args);
        Log.Info($"Shadow {s.Server}#{s.SessionId} ({s.QualifiedUser}) control={control} consent={consentPrompt}");
    }

    public static void RdpAdmin(string server) => Start("mstsc.exe", $"/v:{server} /admin");
    public static void Rdp(string host) => Start("mstsc.exe", $"/v:{host}");
    public static void Ping(string host) => Start("cmd.exe", $"/k ping -t {host}");
    public static void Explorer(string path) => Start("explorer.exe", $"\"{path}\"");
    public static void CompMgmt(string server) => Start("mmc.exe", $"compmgmt.msc /computer:{server}");
    public static void Services(string server) => Start("mmc.exe", $"services.msc /computer:{server}");
    public static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static void Start(string file, string args)
        => Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
}

public static class HistoryService
{
    private const string LogName = "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational";

    public static List<HistoryEntry> Read(string server, int days, string? userFilter, CancellationToken ct)
    {
        var ms = (long)TimeSpan.FromDays(Math.Max(1, days)).TotalMilliseconds;
        var xpath = $"*[System[(EventID=21 or EventID=23 or EventID=24 or EventID=25) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        var list = new List<HistoryEntry>();
        using var session = new EventLogSession(server);
        var query = new EventLogQuery(LogName, PathType.LogName, xpath) { Session = session, ReverseDirection = true };
        using var reader = new EventLogReader(query);
        for (EventRecord? rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
        {
            using (rec)
            {
                ct.ThrowIfCancellationRequested();
                var props = rec.Properties;
                var user = props.Count > 0 ? props[0].Value?.ToString() ?? "" : "";
                if (!string.IsNullOrEmpty(userFilter) && !user.Contains(userFilter, StringComparison.OrdinalIgnoreCase)) continue;
                int sid = 0;
                if (props.Count > 1) int.TryParse(props[1].Value?.ToString(), out sid);
                var ip = props.Count > 2 ? props[2].Value?.ToString() ?? "" : "";
                list.Add(new HistoryEntry
                {
                    Time = rec.TimeCreated ?? DateTime.MinValue,
                    Server = server,
                    EventId = rec.Id,
                    Event = rec.Id switch
                    {
                        21 => Loc.T("Event.Logon"),
                        23 => Loc.T("Event.Logoff"),
                        24 => Loc.T("Event.Disconnected"),
                        25 => Loc.T("Event.Reconnected"),
                        _ => rec.Id.ToString(),
                    },
                    User = user,
                    SessionId = sid,
                    ClientIp = ip == "LOCAL" ? "" : ip,
                });
            }
        }
        return list;
    }
}
