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

    public static List<ProfileEntry> Scan(IEnumerable<string> updShares, IEnumerable<string> fsLogixShares, CancellationToken ct, Action<string>? progress = null)
    {
        var result = new List<ProfileEntry>();
        foreach (var share in updShares)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(share);
            try
            {
                foreach (var f in Directory.EnumerateFiles(share, "UVHD-*.vhd*", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    var fi = new FileInfo(f);
                    if (fi.Name.Contains("template", StringComparison.OrdinalIgnoreCase)) continue;
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
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Partage UPD {share} illisible", ex);
                result.Add(new ProfileEntry { Kind = "UPD", Share = share, FileName = "ERREUR : " + ex.Message });
            }
        }

        foreach (var share in fsLogixShares)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(share);
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(share))
                {
                    ct.ThrowIfCancellationRequested();
                    var di = new DirectoryInfo(dir);
                    var sid = SidRx.Match(di.Name).Value;
                    var userFromName = SidRx.Replace(di.Name, "").Trim('_', ' ');
                    var vhds = di.EnumerateFiles("*.vhd*", SearchOption.TopDirectoryOnly).ToList();
                    if (vhds.Count == 0 && string.IsNullOrEmpty(sid)) continue;
                    var user = !string.IsNullOrEmpty(userFromName) ? userFromName : ResolveSid(sid);
                    if (vhds.Count == 0)
                    {
                        result.Add(new ProfileEntry { Kind = "FSLogix", Share = share, Path = di.FullName, FileName = "(aucun VHDX)", Sid = sid, User = user, Modified = di.LastWriteTime });
                        continue;
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
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Partage FSLogix {share} illisible", ex);
                result.Add(new ProfileEntry { Kind = "FSLogix", Share = share, FileName = "ERREUR : " + ex.Message });
            }
        }
        return result;
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
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
        if (p.Kind == "UPD") File.Delete(p.Path);
        else
        {
            var dir = Path.GetDirectoryName(p.Path);
            if (File.Exists(p.Path)) File.Delete(p.Path);
            if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        Log.Warn($"Profil supprimé : {p.Path}");
    }
}
