using System.Collections.Concurrent;
using System.Management;
using Dahu.Core;
using Dahu.Models;

namespace Dahu.Services;

public static class WmiService
{
    public static ManagementScope Connect(string server, string ns, TimeSpan timeout)
    {
        var opts = new ConnectionOptions
        {
            Authentication = AuthenticationLevel.PacketPrivacy,
            Impersonation = ImpersonationLevel.Impersonate,
            EnablePrivileges = true,
            Timeout = timeout,
        };
        var scope = new ManagementScope($@"\\{server}\{ns}", opts);
        scope.Connect();
        return scope;
    }

    private static readonly ConcurrentDictionary<string, ManagementScope> ScopeCache = new(StringComparer.OrdinalIgnoreCase);

    private static ManagementScope CachedScope(string server, string ns, TimeSpan timeout)
    {
        var key = server + "|" + ns;
        if (ScopeCache.TryGetValue(key, out var s) && s.IsConnected) return s;
        s = Connect(server, ns, timeout);
        ScopeCache[key] = s;
        return s;
    }

    private static void WithCachedScope(string server, string ns, TimeSpan timeout, CancellationToken ct, Action<ManagementScope> work)
    {
        var key = server + "|" + ns;
        var cached = ScopeCache.ContainsKey(key);
        try { work(CachedScope(server, ns, timeout)); }
        catch (Exception) when (cached && !ct.IsCancellationRequested)
        {
            ScopeCache.TryRemove(key, out _);
            ct.ThrowIfCancellationRequested();
            work(CachedScope(server, ns, timeout));
        }
    }

    private static IEnumerable<ManagementObject> Query(ManagementScope scope, string wql, TimeSpan timeout)
    {
        var q = new ObjectQuery(wql);
        var eo = new EnumerationOptions { Timeout = timeout, ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(scope, q, eo);
        foreach (ManagementObject mo in searcher.Get()) yield return mo;
    }

    private static DateTime? WmiDate(object? o)
    {
        try { return o is string s && s.Length > 0 ? ManagementDateTimeConverter.ToDateTime(s) : null; } catch { return null; }
    }

    public static void FillServerInfo(RdsServer srv, bool perf, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        WithCachedScope(srv.Name, "root\\cimv2", timeout, ct, scope => FillOsInfo(srv, scope, perf, timeout, ct));

        ct.ThrowIfCancellationRequested();
        try
        {
            WithCachedScope(srv.Name, "root\\cimv2\\TerminalServices", timeout, ct, ts =>
            {
                foreach (var t in Query(ts, "SELECT SessionBrokerDrainMode, Logons FROM Win32_TerminalServiceSetting", timeout))
                {
                    srv.DrainMode = Convert.ToInt32(t["SessionBrokerDrainMode"] ?? 0);
                    srv.LogonsEnabled = Convert.ToInt32(t["Logons"] ?? 0) == 0;
                }
            });
            srv.Mode = !srv.LogonsEnabled ? Loc.T("Mode.LogonsDisabled") : srv.DrainMode switch
            {
                0 => Loc.T("Mode.Production"),
                1 => Loc.T("Mode.DrainReboot"),
                2 => Loc.T("Mode.Drain"),
                _ => Loc.T("Mode.Unknown"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"{srv.Name}: lecture TerminalServiceSetting impossible : {ex.Message}");
            srv.Mode = Loc.T("Mode.Unknown");
        }
    }

    private static void FillOsInfo(RdsServer srv, ManagementScope scope, bool perf, TimeSpan timeout, CancellationToken ct)
    {
        foreach (var os in Query(scope, "SELECT Caption, Version, FreePhysicalMemory, TotalVisibleMemorySize, LastBootUpTime FROM Win32_OperatingSystem", timeout))
        {
            var caption = (os["Caption"] as string ?? "").Replace("Microsoft ", "").Trim();
            srv.Os = $"{caption} ({os["Version"]})";
            srv.BootTime = WmiDate(os["LastBootUpTime"]);
            if (perf)
            {
                var free = Convert.ToDouble(os["FreePhysicalMemory"] ?? 0) / 1024.0;
                var total = Convert.ToDouble(os["TotalVisibleMemorySize"] ?? 0) / 1024.0;
                if (total > 0)
                {
                    srv.MemoryPercent = Math.Round((total - free) / total * 100, 1);
                    srv.MemoryText = $"{(total - free) / 1024:0.0} / {total / 1024:0.0} Go";
                }
            }
        }

        if (perf)
        {
            ct.ThrowIfCancellationRequested();
            var loads = new List<double>();
            foreach (var p in Query(scope, "SELECT LoadPercentage FROM Win32_Processor", timeout))
                if (p["LoadPercentage"] != null) loads.Add(Convert.ToDouble(p["LoadPercentage"]));
            srv.Cpu = loads.Count > 0 ? Math.Round(loads.Average(), 0) : null;

            ct.ThrowIfCancellationRequested();
            foreach (var d in Query(scope, "SELECT FreeSpace, Size FROM Win32_LogicalDisk WHERE DeviceID='C:'", timeout))
            {
                var fs = Convert.ToDouble(d["FreeSpace"] ?? 0);
                var sz = Convert.ToDouble(d["Size"] ?? 0);
                if (sz > 0)
                {
                    srv.DiskFreePercent = Math.Round(fs / sz * 100, 1);
                    srv.DiskText = $"{fs / (1L << 30):0.0} Go ({srv.DiskFreePercent:0}%)";
                }
            }
        }
    }

    public static void SetDrainMode(string server, int mode, TimeSpan timeout)
    {
        try
        {
            var ts = Connect(server, "root\\cimv2\\TerminalServices", timeout);
            foreach (var t in Query(ts, "SELECT * FROM Win32_TerminalServiceSetting", timeout))
            {
                t["SessionBrokerDrainMode"] = mode;
                t.Put(new PutOptions { Type = PutType.UpdateOnly });
                return;
            }
            throw new InvalidOperationException("Win32_TerminalServiceSetting introuvable");
        }
        catch (Exception ex)
        {
            Log.Warn($"{server}: SessionBrokerDrainMode via WMI impossible ({ex.Message}), repli sur change logon");
            var arg = mode switch { 0 => "/enable", 1 => "/drainuntilrestart", _ => "/drain" };
            RunRemote(server, $"cmd.exe /c change logon {arg}", timeout);
        }
    }

    public static int RunRemote(string server, string commandLine, TimeSpan timeout)
    {
        var scope = Connect(server, "root\\cimv2", timeout);
        using var cls = new ManagementClass(scope, new ManagementPath("Win32_Process"), null);
        using var inParams = cls.GetMethodParameters("Create");
        inParams["CommandLine"] = commandLine;
        using var outParams = cls.InvokeMethod("Create", inParams, null);
        var rv = Convert.ToInt32(outParams["ReturnValue"]);
        if (rv != 0) throw new InvalidOperationException(string.Format(Loc.T("Err.ProcessCreate"), rv));
        return Convert.ToInt32(outParams["ProcessId"]);
    }

    public static List<RdsProcess> GetProcesses(string server, int? sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var scope = Connect(server, "root\\cimv2", timeout);
        var where = sessionId is { } sid ? $" WHERE SessionId={sid}" : "";
        var list = new List<RdsProcess>();
        foreach (var p in Query(scope, "SELECT ProcessId, SessionId, Name, WorkingSetSize, KernelModeTime, UserModeTime, CreationDate, ExecutablePath FROM Win32_Process" + where, timeout))
        {
            ct.ThrowIfCancellationRequested();
            var proc = new RdsProcess
            {
                Server = server,
                Pid = Convert.ToInt32(p["ProcessId"] ?? 0),
                SessionId = Convert.ToInt32(p["SessionId"] ?? 0),
                Name = p["Name"] as string ?? "",
                WorkingSetMb = Math.Round(Convert.ToDouble(p["WorkingSetSize"] ?? 0) / (1024 * 1024), 1),
                Started = WmiDate(p["CreationDate"]),
                Path = p["ExecutablePath"] as string ?? "",
            };
            var kt = Convert.ToUInt64(p["KernelModeTime"] ?? 0UL);
            var ut = Convert.ToUInt64(p["UserModeTime"] ?? 0UL);
            proc.CpuTime = TimeSpan.FromTicks((long)(kt + ut));
            try
            {
                using var o = p.InvokeMethod("GetOwner", null, null);
                if (o != null && Convert.ToInt32(o["ReturnValue"]) == 0)
                {
                    var d = o["Domain"] as string; var u = o["User"] as string;
                    proc.Owner = string.IsNullOrEmpty(d) ? u ?? "" : $"{d}\\{u}";
                }
            }
            catch { }
            list.Add(proc);
        }
        return list;
    }

    public static void KillProcess(string server, int pid, TimeSpan timeout)
    {
        var scope = Connect(server, "root\\cimv2", timeout);
        foreach (var p in Query(scope, $"SELECT ProcessId FROM Win32_Process WHERE ProcessId={pid}", timeout))
        {
            p.InvokeMethod("Terminate", new object[] { 1 });
            return;
        }
        throw new InvalidOperationException(string.Format(Loc.T("Err.ProcessNotFound"), pid));
    }

    public static void Shutdown(string server, bool reboot, TimeSpan timeout)
    {
        var scope = Connect(server, "root\\cimv2", timeout);
        foreach (var os in Query(scope, "SELECT * FROM Win32_OperatingSystem", timeout))
        {
            using var inp = os.GetMethodParameters("Win32Shutdown");
            inp["Flags"] = reboot ? 6 : 12;
            inp["Reserved"] = 0;
            using var r = os.InvokeMethod("Win32Shutdown", inp, null);
            var rv = Convert.ToInt32(r?["ReturnValue"] ?? 0);
            if (rv != 0) throw new InvalidOperationException(string.Format(Loc.T("Err.Shutdown"), rv));
            return;
        }
    }
}
