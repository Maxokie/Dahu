using Dahu.Core;

namespace Dahu.Services;

public sealed class BrokerSessionInfo
{
    public string HostServer { get; set; } = "";
    public int SessionId { get; set; }
    public string User { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Collection { get; set; } = "";
    public bool RemoteApp { get; set; }
}

public sealed class BrokerData
{
    public string Broker { get; set; } = "";
    public Dictionary<string, string> CollectionsByAlias { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BrokerSessionInfo> Sessions { get; } = new();
    public string? Error { get; set; }
}

public static class BrokerService
{
    public static BrokerData Query(string broker, TimeSpan timeout, CancellationToken ct)
    {
        var data = new BrokerData { Broker = broker };

        var rdms = WmiService.Connect(broker, "root\\cimv2\\rdms", timeout);
        ct.ThrowIfCancellationRequested();

        foreach (var c in WmiService.Query(rdms, "SELECT Name, Alias FROM Win32_RDSHCollection", timeout))
            data.CollectionsByAlias[c["Alias"] as string ?? ""] = c["Name"] as string ?? "";

        ct.ThrowIfCancellationRequested();
        foreach (var h in WmiService.Query(rdms, "SELECT Name, CollectionAlias FROM Win32_RDSHServer", timeout))
        {
            var name = h["Name"] as string ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            var alias = h["CollectionAlias"] as string ?? "";
            data.Hosts[name] = data.CollectionsByAlias.TryGetValue(alias, out var cn) ? cn : alias;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var cimv2 = WmiService.Connect(broker, "root\\cimv2", timeout);
            foreach (var o in WmiService.Query(cimv2, "SELECT HostServer, ServerName, UnifiedSessionId, SessionId, UserName, DomainName, CollectionAlias, ApplicationType FROM Win32_SessionDirectorySessionEx", timeout))
            {
                var host = o["HostServer"] as string;
                if (string.IsNullOrEmpty(host)) host = o["ServerName"] as string ?? "";
                var alias = o["CollectionAlias"] as string ?? "";
                int sid = 0;
                try { sid = Convert.ToInt32(o["UnifiedSessionId"] ?? o["SessionId"] ?? 0); } catch { }
                data.Sessions.Add(new BrokerSessionInfo
                {
                    HostServer = host,
                    SessionId = sid,
                    User = o["UserName"] as string ?? "",
                    Domain = o["DomainName"] as string ?? "",
                    Collection = data.CollectionsByAlias.TryGetValue(alias, out var cn) ? cn : alias,
                    RemoteApp = Convert.ToInt32(o["ApplicationType"] ?? 0) != 0,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Broker {broker}: Win32_SessionDirectorySessionEx indisponible : {ex.Message}");
        }

        Log.Info($"Broker {broker}: {data.CollectionsByAlias.Count} collection(s), {data.Hosts.Count} hôte(s), {data.Sessions.Count} session(s)");
        return data;
    }
}
