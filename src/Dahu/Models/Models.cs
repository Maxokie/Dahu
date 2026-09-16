using System.ComponentModel;
using System.Runtime.CompilerServices;
using Dahu.Core;

namespace Dahu.Models;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum SessionState
{
    Active = 0, Connected = 1, ConnectQuery = 2, Shadow = 3, Disconnected = 4,
    Idle = 5, Listen = 6, Reset = 7, Down = 8, Init = 9
}

public sealed class RdsSession : Observable
{
    public string Server { get; set; } = "";
    public int SessionId { get; set; }
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    public SessionState State { get; set; }
    public string StationName { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public string Protocol { get; set; } = "";
    public int ClientBuild { get; set; }
    public string Resolution { get; set; } = "";
    public DateTime? LogonTime { get; set; }
    public DateTime? ConnectTime { get; set; }
    public DateTime? DisconnectTime { get; set; }
    public DateTime? LastInputTime { get; set; }
    public TimeSpan? IdleTime { get; set; }
    public string Collection { get; set; } = "";
    public string AppType { get; set; } = "";
    public string InitialProgram { get; set; } = "";
    public string ClientDirectory { get; set; } = "";
    public long IncomingBytes { get; set; }
    public long OutgoingBytes { get; set; }

    public string QualifiedUser => string.IsNullOrEmpty(Domain) ? UserName : $"{Domain}\\{UserName}";
    public string Key => $"{Net.ShortName(Server).ToUpperInvariant()}#{SessionId}";
    public bool IsUserSession => !string.IsNullOrEmpty(UserName);
    public bool IsActive => State == SessionState.Active;

    public string StateText => State switch
    {
        SessionState.Active => Loc.T("State.Active"),
        SessionState.Connected => Loc.T("State.Connected"),
        SessionState.ConnectQuery => Loc.T("State.ConnectQuery"),
        SessionState.Shadow => Loc.T("State.Shadow"),
        SessionState.Disconnected => Loc.T("State.Disconnected"),
        SessionState.Idle => Loc.T("State.Idle"),
        SessionState.Listen => Loc.T("State.Listen"),
        SessionState.Reset => Loc.T("State.Reset"),
        SessionState.Down => Loc.T("State.Down"),
        _ => Loc.T("State.Init"),
    };

    public string IdleText => IdleTime is { } t ? (t.TotalDays >= 1 ? $"{(int)t.TotalDays}j {t.Hours:00}:{t.Minutes:00}" : $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}") : "";
}

public sealed class RdsServer : Observable
{
    private string _status = "";
    private int _sessionCount, _activeCount, _disconnectedCount;
    private double? _cpu, _memPct, _diskFreePct;
    private string _os = "", _memText = "", _diskText = "", _mode = "", _collection = "";
    private DateTime? _boot;
    private bool _online = true;
    private int? _drainMode;
    private bool _logonsEnabled = true;

    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Collection { get => _collection; set => Set(ref _collection, value); }
    public string Os { get => _os; set => Set(ref _os, value); }
    public int SessionCount { get => _sessionCount; set => Set(ref _sessionCount, value); }
    public int ActiveCount { get => _activeCount; set => Set(ref _activeCount, value); }
    public int DisconnectedCount { get => _disconnectedCount; set => Set(ref _disconnectedCount, value); }
    public double? Cpu { get => _cpu; set => Set(ref _cpu, value); }
    public double? MemoryPercent { get => _memPct; set => Set(ref _memPct, value); }
    public string MemoryText { get => _memText; set => Set(ref _memText, value); }
    public double? DiskFreePercent { get => _diskFreePct; set => Set(ref _diskFreePct, value); }
    public string DiskText { get => _diskText; set => Set(ref _diskText, value); }
    public DateTime? BootTime { get => _boot; set => Set(ref _boot, value); }
    public string Mode { get => _mode; set => Set(ref _mode, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Online { get => _online; set => Set(ref _online, value); }
    public int? DrainMode { get => _drainMode; set => Set(ref _drainMode, value); }
    public bool LogonsEnabled { get => _logonsEnabled; set => Set(ref _logonsEnabled, value); }
    public DateTime? LastSeen { get; set; }

    public bool InMaintenance => (DrainMode ?? 0) != 0 || !LogonsEnabled;
}

public sealed class RdsProcess
{
    public string Server { get; set; } = "";
    public int Pid { get; set; }
    public int SessionId { get; set; }
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "";
    public double WorkingSetMb { get; set; }
    public TimeSpan CpuTime { get; set; }
    public DateTime? Started { get; set; }
    public string Path { get; set; } = "";
    public string CpuTimeText => $"{(int)CpuTime.TotalHours:00}:{CpuTime.Minutes:00}:{CpuTime.Seconds:00}";
}

public sealed class ProfileEntry
{
    public string Kind { get; set; } = "";
    public string Share { get; set; } = "";
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sid { get; set; } = "";
    public string User { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime Modified { get; set; }
    public bool Mounted { get; set; }
    public string SizeText => SizeBytes >= 1L << 30 ? $"{SizeBytes / (double)(1L << 30):0.00} Go" : $"{SizeBytes / (double)(1L << 20):0} Mo";
    public string MountedText => Mounted ? "Oui" : "";
}

public sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public string Server { get; set; } = "";
    public int EventId { get; set; }
    public string Event { get; set; } = "";
    public string User { get; set; } = "";
    public int SessionId { get; set; }
    public string ClientIp { get; set; } = "";
}

public sealed class AdUserInfo
{
    public bool Found { get; set; }
    public string SamAccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Description { get; set; } = "";
    public string Department { get; set; } = "";
    public string Title { get; set; } = "";
    public string Company { get; set; } = "";
    public string Office { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string Manager { get; set; } = "";
    public string DistinguishedName { get; set; } = "";
    public string Sid { get; set; } = "";
    public bool Enabled { get; set; }
    public bool LockedOut { get; set; }
    public DateTime? LastLogon { get; set; }
    public DateTime? LastPasswordSet { get; set; }
    public DateTime? AccountExpires { get; set; }
    public bool PasswordNeverExpires { get; set; }
    public bool MustChangePassword { get; set; }
    public string HomeDirectory { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public string TsProfilePath { get; set; } = "";
    public string TsHomeDirectory { get; set; } = "";
    public List<string> Groups { get; set; } = new();
    public DateTime? WhenCreated { get; set; }
}

public sealed class ActionResult
{
    public int Ok { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();
    public string Summary => Errors.Count == 0
        ? string.Format(Loc.T("Result.Done"), Ok, Failed)
        : string.Format(Loc.T("Result.Done"), Ok, Failed) + "\n\n" + string.Join("\n", Errors.Take(15));
}
