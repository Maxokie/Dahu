using System.Collections.Concurrent;
using Dahu.Core;
using Dahu.Models;

namespace Dahu.Services;

public sealed class RefreshResult
{
    public List<RdsServer> Servers { get; } = new();
    public List<RdsSession> Sessions { get; } = new();
    public List<string> Errors { get; } = new();
    public int FailedServers { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

public static class FarmService
{
    private static readonly ConcurrentDictionary<string, RdsServer> ServerCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<RefreshResult> RefreshAsync(AppConfig cfg, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new RefreshResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timeout = cfg.Timeout;
        var hosts = new Dictionary<string, (string source, string collection)>(StringComparer.OrdinalIgnoreCase);
        var brokerSessions = new List<BrokerSessionInfo>();

        var manual = Loc.T("Source.Manual");
        foreach (var s in cfg.Servers.Select(x => x.Trim()).Where(x => x.Length > 0))
            if (!hosts.Keys.Any(h => Net.SameHost(h, s))) hosts[s] = (manual, "");

        var brokerTasks = cfg.Brokers.Where(b => !string.IsNullOrWhiteSpace(b)).Select(async b =>
        {
            b = b.Trim();
            progress?.Report($"Broker {b}...");
            try
            {
                if (!await Net.PortOpenAsync(b, 135, TimeSpan.FromSeconds(3), ct)) throw new TimeoutException(Loc.T("Err.Port135Broker"));
                return await Net.WithTimeout(c => BrokerService.Query(b, timeout, c), timeout + TimeSpan.FromSeconds(4), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"Broker {b} injoignable", ex);
                return new BrokerData { Broker = b, Error = ex.Message };
            }
        }).ToList();

        foreach (var bd in await Task.WhenAll(brokerTasks))
        {
            if (bd.Error != null) { result.Errors.Add($"Broker {bd.Broker} : {bd.Error}"); continue; }
            foreach (var (host, coll) in bd.Hosts)
            {
                var key = hosts.Keys.FirstOrDefault(h => Net.SameHost(h, host));
                hosts[key ?? host] = (key == null ? Loc.T("Source.Broker") : hosts[key].source, coll);
            }
            brokerSessions.AddRange(bd.Sessions);
        }

        foreach (var ex in cfg.ExcludedServers)
            foreach (var k in hosts.Keys.Where(h => Net.SameHost(h, ex)).ToList()) hosts.Remove(k);

        var domainFilter = cfg.DomainFilter.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToList();
        var sessions = new ConcurrentBag<RdsSession>();
        var servers = new ConcurrentBag<RdsServer>();
        int failed = 0;
        var errors = new ConcurrentBag<string>();

        using var sem = new SemaphoreSlim(24);
        var hostTasks = hosts.Select(async kv =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var name = kv.Key;
                var srv = ServerCache.GetOrAdd(name, n => new RdsServer { Name = n });
                srv.Source = kv.Value.source;
                if (!string.IsNullOrEmpty(kv.Value.collection)) srv.Collection = kv.Value.collection;
                progress?.Report(name);

                var p445 = Net.PortOpenAsync(name, 445, TimeSpan.FromSeconds(3), ct);
                var p135 = Net.PortOpenAsync(name, 135, TimeSpan.FromSeconds(3), ct);
                await Task.WhenAll(p445, p135);

                var wtsTask = Task.Run(async () =>
                {
                    if (!await p445) throw new TimeoutException(Loc.T("Err.Port445"));
                    return await Net.WithTimeout(c => WtsApi.EnumerateSessions(name, cfg.ShowSystemSessions, c), timeout, ct);
                }, ct);
                var wmiTask = Task.Run(async () =>
                {
                    if (!await p135) throw new TimeoutException(Loc.T("Err.Port135"));
                    await Net.WithTimeout(c => WmiService.FillServerInfo(srv, cfg.CollectPerformance, timeout, c), timeout + TimeSpan.FromSeconds(4), ct);
                }, ct);

                try
                {
                    var list = await wtsTask;
                    if (domainFilter.Count > 0)
                        list = list.Where(s => !s.IsUserSession || domainFilter.Any(d => d.Equals(s.Domain, StringComparison.OrdinalIgnoreCase))).ToList();
                    foreach (var s in list) sessions.Add(s);
                    srv.SessionCount = list.Count(s => s.IsUserSession);
                    srv.ActiveCount = list.Count(s => s.IsUserSession && s.State == SessionState.Active);
                    srv.DisconnectedCount = list.Count(s => s.IsUserSession && s.State == SessionState.Disconnected);
                    srv.Online = true;
                    srv.Status = "";
                    srv.LastSeen = DateTime.Now;
                }
                catch (OperationCanceledException) { Net.Observe(wmiTask); throw; }
                catch (Exception ex)
                {
                    srv.Online = false;
                    srv.Status = ex.Message;
                    srv.SessionCount = 0; srv.ActiveCount = 0; srv.DisconnectedCount = 0;
                    Interlocked.Increment(ref failed);
                    errors.Add($"{name} : {ex.Message}");
                    Log.Warn($"{name}: sessions indisponibles : {ex.Message}");
                }

                try { await wmiTask; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (srv.Online) srv.Status = "WMI : " + ex.Message;
                    srv.Cpu = null; srv.MemoryPercent = null;
                    Log.Warn($"{name}: WMI indisponible : {ex.Message}");
                }
                servers.Add(srv);
            }
            finally { sem.Release(); }
        }).ToList();

        await Task.WhenAll(hostTasks);

        var bsIndex = new Dictionary<string, BrokerSessionInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in brokerSessions) bsIndex[$"{Net.ShortName(b.HostServer)}#{b.SessionId}"] = b;
        var collByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in hosts) collByHost[Net.ShortName(kv.Key)] = kv.Value.collection;

        foreach (var s in sessions)
        {
            if (bsIndex.TryGetValue($"{Net.ShortName(s.Server)}#{s.SessionId}", out var b))
            {
                s.Collection = b.Collection;
                s.AppType = b.RemoteApp ? Loc.T("Type.RemoteApp") : Loc.T("Type.Desktop");
            }
            else
            {
                if (collByHost.TryGetValue(Net.ShortName(s.Server), out var c)) s.Collection = c;
                if (s.IsUserSession && s.Protocol != "Console")
                    s.AppType = s.InitialProgram.Contains("rdpinit", StringComparison.OrdinalIgnoreCase) || s.InitialProgram.Contains("||", StringComparison.Ordinal)
                        ? Loc.T("Type.RemoteApp") : Loc.T("Type.Desktop");
            }
        }

        result.Servers.AddRange(servers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase));
        result.Sessions.AddRange(sessions.OrderBy(s => s.Server, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.SessionId));
        result.Errors.AddRange(errors);
        result.FailedServers = failed;

        foreach (var k in ServerCache.Keys.Where(k => !hosts.ContainsKey(k)).ToList()) ServerCache.TryRemove(k, out _);
        WmiService.Prune(hosts.Keys);

        Log.Info($"Actualisation : {result.Sessions.Count} session(s), {result.Servers.Count} serveur(s), {failed} en erreur - {sw.Elapsed.TotalSeconds:0.0}s");
        return result;
    }

    public static async Task<ActionResult> ForEachAsync<T>(IEnumerable<T> items, Func<T, string> label, Action<T> action, TimeSpan timeout)
    {
        var r = new ActionResult();
        var tasks = items.Select(async item =>
        {
            try
            {
                await Net.WithTimeout(_ => action(item), timeout);
                lock (r) r.Ok++;
            }
            catch (Exception ex)
            {
                lock (r) { r.Failed++; r.Errors.Add($"{label(item)} : {ex.Message}"); }
                Log.Error($"Action échouée sur {label(item)}", ex);
            }
        });
        await Task.WhenAll(tasks);
        return r;
    }
}
