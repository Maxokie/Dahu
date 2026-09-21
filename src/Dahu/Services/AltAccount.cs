using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Dahu.Core;
using Microsoft.Win32.SafeHandles;

namespace Dahu.Services;

public static class AltAccount
{
    private const string ArgName = "/alt";

    public static string? ActiveUser { get; private set; }

    public static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(ArgName, StringComparison.OrdinalIgnoreCase)) continue;
            ActiveUser = i + 1 < args.Length ? args[i + 1] : "?";
            return;
        }
    }

    public static bool IsChild => ActiveUser != null;

    public static bool ShouldRelaunch(AppConfig cfg)
    {
        if (IsChild) return false;
        if (!cfg.UseAltAccount || string.IsNullOrWhiteSpace(cfg.AltAccountUser)) return false;
        if (!cfg.AltAccountNetOnly)
        {
            var current = Environment.UserDomainName + "\\" + Environment.UserName;
            if (current.Equals(cfg.AltAccountUser.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static void Launch(string user, string password, bool netOnly)
    {
        user = user.Trim();
        if (user.Length == 0) throw new InvalidOperationException(Loc.T("Err.NoAccount"));

        var (name, domain) = SplitUser(user);

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException(Loc.T("Err.ExeNotFound"));
        var cmd = new StringBuilder();
        cmd.Append('"').Append(exe).Append("\" ").Append(ArgName).Append(" \"").Append(user).Append('"');
        cmd.Append(" /config \"").Append(ConfigService.FilePath).Append('"');

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        uint flags = netOnly ? LOGON_NETCREDENTIALS_ONLY : LOGON_WITH_PROFILE;
        if (!CreateProcessWithLogonW(name, domain, password, flags, null, cmd, CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero, System.IO.Path.GetDirectoryName(exe), ref si, out var pi))
        {
            var err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, new Win32Exception(err).Message);
        }
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        Log.Info($"Relance sous {user} (netonly={netOnly}) : PID {pi.dwProcessId}");
    }

    public sealed record TestResult(bool Ok, string Report);

    public static async Task<TestResult> TestAsync(string user, string password, AppConfig cfg)
    {
        user = user.Trim();
        var (name, domain) = SplitUser(user);
        var lines = new List<string>();
        var ok = true;

        if (LogonUserW(name, domain, password, LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out var probe))
        {
            probe.Dispose();
            lines.Add(Loc.T("Alt.TestCredOk"));
        }
        else
        {
            var err = Marshal.GetLastWin32Error();
            var msg = new Win32Exception(err).Message;
            if (err is ERROR_LOGON_FAILURE or ERROR_ACCOUNT_RESTRICTION or ERROR_ACCOUNT_DISABLED
                or ERROR_PASSWORD_EXPIRED or ERROR_PASSWORD_MUST_CHANGE or ERROR_ACCOUNT_EXPIRED or ERROR_ACCOUNT_LOCKED_OUT)
            {
                lines.Add(string.Format(Loc.T("Alt.TestCredFail"), msg));
                return new TestResult(false, string.Join(Environment.NewLine, lines));
            }
            lines.Add(string.Format(Loc.T("Alt.TestCredSkip"), msg));
        }

        var target = cfg.Servers.FirstOrDefault() ?? cfg.Brokers.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
        {
            lines.Add(Loc.T("Alt.TestNoServer"));
            return new TestResult(ok, string.Join(Environment.NewLine, lines));
        }

        if (!LogonUserW(name, domain, password, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out var token))
        {
            var err = Marshal.GetLastWin32Error();
            lines.Add(string.Format(Loc.T("Alt.TestAccessFail"), target, new Win32Exception(err).Message));
            return new TestResult(false, string.Join(Environment.NewLine, lines));
        }
        using (token)
        {
            try
            {
                var count = await Net.WithTimeout(
                    c => WindowsIdentity.RunImpersonated(token, () => WtsApi.EnumerateSessions(target, true, c).Count),
                    cfg.Timeout);
                lines.Add(string.Format(Loc.T("Alt.TestAccessOk"), target, count));
            }
            catch (Exception ex)
            {
                ok = false;
                lines.Add(string.Format(Loc.T("Alt.TestAccessFail"), target, ex.Message));
            }
        }
        Log.Info($"Test du compte {user} : {(ok ? "OK" : "ECHEC")} - {string.Join(" | ", lines)}");
        return new TestResult(ok, string.Join(Environment.NewLine, lines));
    }

    private static (string Name, string? Domain) SplitUser(string user)
    {
        var slash = user.IndexOf('\\');
        if (slash > 0) return (user[(slash + 1)..], user[..slash]);
        if (user.Contains('@')) return (user, null);
        return (user, Environment.UserDomainName);
    }

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var input = Encoding.UTF8.GetBytes(plain);
        var inBlob = new DATA_BLOB { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            if (!CryptProtectData(ref inBlob, "Dahu", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, output, 0, outBlob.cbData);
                return Convert.ToBase64String(output);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally
        {
            Array.Clear(input);
            Marshal.FreeHGlobal(inBlob.pbData);
        }
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
        byte[] input;
        try { input = Convert.FromBase64String(protectedBase64); }
        catch { return null; }
        var inBlob = new DATA_BLOB { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
            {
                Log.Warn("Mot de passe mémorisé illisible (DPAPI) : il sera redemandé.");
                return null;
            }
            try
            {
                var output = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, output, 0, outBlob.cbData);
                return Encoding.UTF8.GetString(output);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(inBlob.pbData); }
    }

    private const uint LOGON_WITH_PROFILE = 0x00000001;
    private const uint LOGON_NETCREDENTIALS_ONLY = 0x00000002;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
    private const int LOGON32_PROVIDER_DEFAULT = 0;
    private const int LOGON32_PROVIDER_WINNT50 = 3;
    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_ACCOUNT_RESTRICTION = 1327;
    private const int ERROR_ACCOUNT_DISABLED = 1331;
    private const int ERROR_PASSWORD_EXPIRED = 1330;
    private const int ERROR_PASSWORD_MUST_CHANGE = 1907;
    private const int ERROR_ACCOUNT_EXPIRED = 1793;
    private const int ERROR_ACCOUNT_LOCKED_OUT = 1909;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithLogonW(string lpUsername, string? lpDomain, string lpPassword, uint dwLogonFlags,
        string? lpApplicationName, StringBuilder lpCommandLine, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUserW(string lpszUsername, string? lpszDomain, string lpszPassword, int dwLogonType,
        int dwLogonProvider, out SafeAccessTokenHandle phToken);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
