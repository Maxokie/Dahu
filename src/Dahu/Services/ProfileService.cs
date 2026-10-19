using System.Collections.Concurrent;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Dahu.Core;
using Dahu.Models;

namespace Dahu.Services;

public static class ProfileService
{
    private static readonly Regex SidRx = new(@"S-1-5-21-\d+-\d+-\d+-\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Dictionary<string, string> SidCache = new(StringComparer.OrdinalIgnoreCase);
    private const int Parallelism = 8;

    public static List<ProfileEntry> Scan(IEnumerable<string> updShares, IEnumerable<string> fsLogixShares, CancellationToken ct, Action<string>? progress = null)
    {
        var result = new ConcurrentBag<ProfileEntry>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct };

        foreach (var share in updShares)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(share);
            try
            {
                Parallel.ForEach(Directory.EnumerateFiles(share, "UVHD-*.vhd*", SearchOption.TopDirectoryOnly), options, f => Guard(f, () =>
                {
                    var fi = new FileInfo(f);
                    if (fi.Name.Contains("template", StringComparison.OrdinalIgnoreCase)) return;
                    var sid = SidRx.Match(fi.Name).Value;
                    result.Add(new ProfileEntry
                    {
                        Kind = "UPD",
                        Share = share,
                        Path = fi.FullName,
                        FileName = fi.Name,
                        Sid = sid,
                        User = ResolveSid(sid),
                        SizeBytes = fi.Length,
                        Modified = fi.LastWriteTime,
                        Mounted = IsLocked(fi.FullName),
                    });
                }));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ex = Unwrap(ex);
                Log.Error($"Partage UPD {share} illisible", ex);
                result.Add(new ProfileEntry { Kind = "UPD", Share = share, FileName = string.Format(Loc.T("Profiles.Error"), ex.Message) });
            }
        }

        foreach (var share in fsLogixShares)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(share);
            try
            {
                Parallel.ForEach(Directory.EnumerateDirectories(share), options, dir => Guard(dir, () =>
                {
                    var di = new DirectoryInfo(dir);
                    var sid = SidRx.Match(di.Name).Value;
                    var userFromName = SidRx.Replace(di.Name, "").Trim('_', ' ');
                    var vhds = di.EnumerateFiles("*.vhd*", SearchOption.TopDirectoryOnly).ToList();
                    if (vhds.Count == 0 && string.IsNullOrEmpty(sid)) return;
                    var user = !string.IsNullOrEmpty(userFromName) ? userFromName : ResolveSid(sid);
                    if (vhds.Count == 0)
                    {
                        result.Add(new ProfileEntry { Kind = "FSLogix", Share = share, Path = di.FullName, FileName = Loc.T("Profiles.NoVhd"), Sid = sid, User = user, Modified = di.LastWriteTime });
                        return;
                    }
                    foreach (var fi in vhds)
                    {
                        result.Add(new ProfileEntry
                        {
                            Kind = fi.Name.StartsWith("ODFC", StringComparison.OrdinalIgnoreCase) ? "FSLogix ODFC" : "FSLogix",
                            Share = share,
                            Path = fi.FullName,
                            FileName = fi.Name,
                            Sid = sid,
                            User = user,
                            SizeBytes = fi.Length,
                            Modified = fi.LastWriteTime,
                            Mounted = IsLocked(fi.FullName),
                        });
                    }
                }));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ex = Unwrap(ex);
                Log.Error($"Partage FSLogix {share} illisible", ex);
                result.Add(new ProfileEntry { Kind = "FSLogix", Share = share, FileName = string.Format(Loc.T("Profiles.Error"), ex.Message) });
            }
        }
        return result.ToList();
    }

    private static void Guard(string item, Action work)
    {
        try { work(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"Profil ignoré {item} : {ex.Message}"); }
    }

    private static Exception Unwrap(Exception ex)
        => ex is AggregateException ae && ae.InnerExceptions.Count == 1 ? Unwrap(ae.InnerExceptions[0]) : ex;

    private static bool IsLocked(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    public static string ResolveSid(string sid)
    {
        if (string.IsNullOrEmpty(sid)) return "";
        lock (SidCache)
            if (SidCache.TryGetValue(sid, out var cached)) return cached;
        string name = "";
        try
        {
            name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch
        {
            try { name = AdService.FindBySid(sid)?.SamAccountName ?? ""; } catch { }
        }
        lock (SidCache) SidCache[sid] = name;
        return name;
    }

    public static void Delete(ProfileEntry p)
    {
        if (Directory.Exists(p.Path)) Directory.Delete(p.Path, true);
        else
        {
            File.Delete(p.Path);
            if (p.Kind != "UPD")
            {
                var dir = Path.GetDirectoryName(p.Path);
                if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
        }
        Log.Warn($"Profil supprimé : {p.Path}");
    }
}
