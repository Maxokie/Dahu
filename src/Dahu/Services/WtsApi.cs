using System.Runtime.InteropServices;
using Dahu.Models;

namespace Dahu.Services;

internal static class WtsApi
{
    private const string Dll = "wtsapi32.dll";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_CLIENT_ADDRESS
    {
        public int AddressFamily;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] Address;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_CLIENT_DISPLAY
    {
        public int HorizontalResolution;
        public int VerticalResolution;
        public int ColorDepth;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTSINFO
    {
        public int State;
        public int SessionId;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    private enum WTS_INFO_CLASS
    {
        WTSInitialProgram = 0, WTSApplicationName = 1, WTSWorkingDirectory = 2, WTSOEMId = 3, WTSSessionId = 4,
        WTSUserName = 5, WTSWinStationName = 6, WTSDomainName = 7, WTSConnectState = 8, WTSClientBuildNumber = 9,
        WTSClientName = 10, WTSClientDirectory = 11, WTSClientProductId = 12, WTSClientHardwareId = 13,
        WTSClientAddress = 14, WTSClientDisplay = 15, WTSClientProtocolType = 16, WTSIdleTime = 17,
        WTSLogonTime = 18, WTSIncomingBytes = 19, WTSOutgoingBytes = 20, WTSIncomingFrames = 21,
        WTSOutgoingFrames = 22, WTSClientInfo = 23, WTSSessionInfo = 24,
    }

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WTSOpenServerW(string pServerName);

    [DllImport(Dll)]
    private static extern void WTSCloseServer(IntPtr hServer);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport(Dll)]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport(Dll, SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, int sessionId, bool bWait);

    [DllImport(Dll, SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, bool bWait);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSSendMessageW(IntPtr hServer, int sessionId, string pTitle, int titleLength,
        string pMessage, int messageLength, int style, int timeout, out int pResponse, bool bWait);

    private static IntPtr Open(string server)
    {
        var h = WTSOpenServerW(server);
        if (h == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"WTSOpenServer({server})");
        return h;
    }

    private static string? QueryString(IntPtr h, int id, WTS_INFO_CLASS cls)
    {
        if (!WTSQuerySessionInformationW(h, id, cls, out var p, out var len) || p == IntPtr.Zero) return null;
        try { return len > 1 ? Marshal.PtrToStringUni(p) : ""; }
        finally { WTSFreeMemory(p); }
    }

    private static T? QueryStruct<T>(IntPtr h, int id, WTS_INFO_CLASS cls) where T : struct
    {
        if (!WTSQuerySessionInformationW(h, id, cls, out var p, out var len) || p == IntPtr.Zero) return null;
        try { return len >= Marshal.SizeOf<T>() ? Marshal.PtrToStructure<T>(p) : null; }
        finally { WTSFreeMemory(p); }
    }

    private static int? QueryInt(IntPtr h, int id, WTS_INFO_CLASS cls, int size)
    {
        if (!WTSQuerySessionInformationW(h, id, cls, out var p, out var len) || p == IntPtr.Zero) return null;
        try { return len >= size ? (size == 2 ? Marshal.ReadInt16(p) : Marshal.ReadInt32(p)) : null; }
        finally { WTSFreeMemory(p); }
    }

    private static DateTime? FromFileTime(long ft)
    {
        if (ft <= 0) return null;
        try { return DateTime.FromFileTime(ft); } catch { return null; }
    }

    public static List<RdsSession> EnumerateSessions(string server, bool includeSystem, CancellationToken ct)
    {
        var list = new List<RdsSession>();
        var h = Open(server);
        try
        {
            if (!WTSEnumerateSessionsW(h, 0, 1, out var pInfo, out var count))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"WTSEnumerateSessions({server})");
            try
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(pInfo + i * size);
                    if (si.State == (int)SessionState.Listen) continue;

                    var s = new RdsSession
                    {
                        Server = server,
                        SessionId = si.SessionId,
                        StationName = si.pWinStationName ?? "",
                        State = (SessionState)si.State,
                    };

                    var info = QueryStruct<WTSINFO>(h, si.SessionId, WTS_INFO_CLASS.WTSSessionInfo);
                    if (info is { } wi)
                    {
                        s.UserName = wi.UserName ?? "";
                        s.Domain = wi.Domain ?? "";
                        s.ConnectTime = FromFileTime(wi.ConnectTime);
                        s.DisconnectTime = FromFileTime(wi.DisconnectTime);
                        s.LogonTime = FromFileTime(wi.LogonTime);
                        s.LastInputTime = FromFileTime(wi.LastInputTime);
                        s.IncomingBytes = (uint)wi.IncomingBytes;
                        s.OutgoingBytes = (uint)wi.OutgoingBytes;
                        var now = FromFileTime(wi.CurrentTime);
                        if (s.LastInputTime is { } li && now is { } n && li <= n) s.IdleTime = n - li;
                    }
                    if (string.IsNullOrEmpty(s.UserName))
                    {
                        s.UserName = QueryString(h, si.SessionId, WTS_INFO_CLASS.WTSUserName) ?? "";
                        s.Domain = QueryString(h, si.SessionId, WTS_INFO_CLASS.WTSDomainName) ?? "";
                    }

                    if (!includeSystem && !s.IsUserSession) continue;

                    s.ClientName = QueryString(h, si.SessionId, WTS_INFO_CLASS.WTSClientName) ?? "";
                    s.ClientDirectory = QueryString(h, si.SessionId, WTS_INFO_CLASS.WTSClientDirectory) ?? "";
                    s.InitialProgram = QueryString(h, si.SessionId, WTS_INFO_CLASS.WTSInitialProgram) ?? "";
                    s.ClientBuild = QueryInt(h, si.SessionId, WTS_INFO_CLASS.WTSClientBuildNumber, 4) ?? 0;
                    var proto = QueryInt(h, si.SessionId, WTS_INFO_CLASS.WTSClientProtocolType, 2) ?? -1;
                    s.Protocol = proto switch { 0 => "Console", 1 => "ICA", 2 => "RDP", _ => "" };

                    var addr = QueryStruct<WTS_CLIENT_ADDRESS>(h, si.SessionId, WTS_INFO_CLASS.WTSClientAddress);
                    if (addr is { } a && a.Address != null)
                    {
                        if (a.AddressFamily == 2) s.ClientIp = $"{a.Address[2]}.{a.Address[3]}.{a.Address[4]}.{a.Address[5]}";
                        else if (a.AddressFamily == 23)
                        {
                            try { s.ClientIp = new System.Net.IPAddress(a.Address.Skip(2).Take(16).ToArray()).ToString(); } catch { }
                        }
                    }

                    var disp = QueryStruct<WTS_CLIENT_DISPLAY>(h, si.SessionId, WTS_INFO_CLASS.WTSClientDisplay);
                    if (disp is { } d && d.HorizontalResolution > 0)
                        s.Resolution = $"{d.HorizontalResolution}x{d.VerticalResolution}x{d.ColorDepth}";

                    list.Add(s);
                }
            }
            finally { WTSFreeMemory(pInfo); }
        }
        finally { WTSCloseServer(h); }
        return list;
    }

    public static void Disconnect(string server, int sessionId)
    {
        var h = Open(server);
        try
        {
            if (!WTSDisconnectSession(h, sessionId, true))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"WTSDisconnectSession({server},{sessionId})");
        }
        finally { WTSCloseServer(h); }
    }

    public static void Logoff(string server, int sessionId)
    {
        var h = Open(server);
        try
        {
            if (!WTSLogoffSession(h, sessionId, true))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"WTSLogoffSession({server},{sessionId})");
        }
        finally { WTSCloseServer(h); }
    }

    public static void SendMessage(string server, int sessionId, string title, string message, int timeoutSeconds = 0)
    {
        var h = Open(server);
        try
        {
            const int MB_OK = 0, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
            if (!WTSSendMessageW(h, sessionId, title, title.Length * 2, message, message.Length * 2,
                    MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST, timeoutSeconds, out _, false))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"WTSSendMessage({server},{sessionId})");
        }
        finally { WTSCloseServer(h); }
    }
}
