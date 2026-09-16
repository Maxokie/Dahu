using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using Dahu.Core;
using Dahu.Models;

namespace Dahu.Services;

public static class AdService
{
    private static PrincipalContext Context(string? domain)
    {
        var ldap = ConfigService.Current.LdapPath;
        if (!string.IsNullOrWhiteSpace(ldap))
        {
            var path = ldap.Replace("LDAP://", "", StringComparison.OrdinalIgnoreCase);
            var slash = path.IndexOf('/');
            return slash > 0
                ? new PrincipalContext(ContextType.Domain, path[..slash], path[(slash + 1)..])
                : new PrincipalContext(ContextType.Domain, null, path);
        }
        return string.IsNullOrWhiteSpace(domain) ? new PrincipalContext(ContextType.Domain) : new PrincipalContext(ContextType.Domain, domain);
    }

    public static AdUserInfo? Find(string domain, string sam)
    {
        using var ctx = Context(domain);
        using var u = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, sam);
        return u == null ? null : Map(u);
    }

    public static AdUserInfo? FindBySid(string sid)
    {
        using var ctx = Context(null);
        using var u = UserPrincipal.FindByIdentity(ctx, IdentityType.Sid, sid);
        return u == null ? null : Map(u, light: true);
    }

    private static AdUserInfo Map(UserPrincipal u, bool light = false)
    {
        var info = new AdUserInfo
        {
            Found = true,
            SamAccountName = u.SamAccountName ?? "",
            DisplayName = u.DisplayName ?? "",
            Email = u.EmailAddress ?? "",
            Description = u.Description ?? "",
            DistinguishedName = u.DistinguishedName ?? "",
            Sid = u.Sid?.Value ?? "",
            Enabled = u.Enabled ?? true,
            LastLogon = u.LastLogon,
            LastPasswordSet = u.LastPasswordSet,
            AccountExpires = u.AccountExpirationDate,
            PasswordNeverExpires = u.PasswordNeverExpires,
            HomeDirectory = u.HomeDirectory ?? "",
            Phone = u.VoiceTelephoneNumber ?? "",
        };
        if (light) return info;

        try { info.LockedOut = u.IsAccountLockedOut(); } catch { }
        try
        {
            if (u.GetUnderlyingObject() is DirectoryEntry de)
            {
                string P(string n) => de.Properties[n]?.Value?.ToString() ?? "";
                info.Department = P("department");
                info.Title = P("title");
                info.Company = P("company");
                info.Office = P("physicalDeliveryOfficeName");
                info.Mobile = P("mobile");
                info.Manager = P("manager");
                info.ProfilePath = P("profilePath");
                if (de.Properties["whenCreated"]?.Value is DateTime wc) info.WhenCreated = wc;
                var pls = de.Properties["pwdLastSet"]?.Value;
                if (pls != null)
                {
                    try
                    {
                        var t = pls.GetType();
                        var high = (int)t.InvokeMember("HighPart", System.Reflection.BindingFlags.GetProperty, null, pls, null)!;
                        var low = (int)t.InvokeMember("LowPart", System.Reflection.BindingFlags.GetProperty, null, pls, null)!;
                        info.MustChangePassword = high == 0 && low == 0;
                    }
                    catch { }
                }
                try
                {
                    info.TsProfilePath = de.InvokeGet("TerminalServicesProfilePath") as string ?? "";
                    info.TsHomeDirectory = de.InvokeGet("TerminalServicesHomeDirectory") as string ?? "";
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Warn("Lecture attributs AD étendus : " + ex.Message); }

        try
        {
            info.Groups = u.GetGroups().Select(g => g.Name ?? "").Where(n => n.Length > 0).OrderBy(n => n).ToList();
        }
        catch (Exception ex) { Log.Warn("Lecture groupes AD : " + ex.Message); }
        return info;
    }

    public static void SetEnabled(string domain, string sam, bool enabled)
    {
        using var ctx = Context(domain);
        using var u = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, sam) ?? throw new InvalidOperationException(Loc.T("Ad.NotFound"));
        u.Enabled = enabled;
        u.Save();
        Log.Warn($"AD : compte {domain}\\{sam} {(enabled ? "activé" : "désactivé")}");
    }

    public static void Unlock(string domain, string sam)
    {
        using var ctx = Context(domain);
        using var u = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, sam) ?? throw new InvalidOperationException(Loc.T("Ad.NotFound"));
        u.UnlockAccount();
        Log.Warn($"AD : compte {domain}\\{sam} déverrouillé");
    }

    public static void ExpirePassword(string domain, string sam)
    {
        using var ctx = Context(domain);
        using var u = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, sam) ?? throw new InvalidOperationException(Loc.T("Ad.NotFound"));
        u.ExpirePasswordNow();
        Log.Warn($"AD : mot de passe de {domain}\\{sam} expiré (changement forcé)");
    }
}
