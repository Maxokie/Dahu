using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Dahu.Core;
using Dahu.Models;
using Dahu.Services;
using Microsoft.Win32;

namespace Dahu.Views;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<RdsSession> _sessions = new();
    private readonly ObservableCollection<RdsServer> _servers = new();
    private readonly ObservableCollection<ProfileEntry> _profiles = new();
    private readonly ObservableCollection<HistoryEntry> _history = new();
    private readonly ICollectionView _sessionsView, _profilesView;
    private readonly DispatcherTimer _timer = new();
    private CancellationTokenSource? _refreshCts;
    private int _refreshing;
    private List<string> _lastErrors = new();
    private AppConfig Cfg => ConfigService.Current;

    public MainWindow()
    {
        InitializeComponent();
        Width = Cfg.WindowWidth; Height = Cfg.WindowHeight;
        if (Cfg.WindowMaximized) WindowState = WindowState.Maximized;

        _sessionsView = CollectionViewSource.GetDefaultView(_sessions);
        _sessionsView.Filter = SessionFilter;
        GridSessions.ItemsSource = _sessionsView;
        GridServers.ItemsSource = _servers;
        _profilesView = CollectionViewSource.GetDefaultView(_profiles);
        _profilesView.Filter = ProfileFilter;
        GridProfiles.ItemsSource = _profilesView;
        GridHistory.ItemsSource = _history;

        TxtUser.Text = $"{Environment.UserDomainName}\\{Environment.UserName} @ {Environment.MachineName}"
                     + (AltAccount.ActiveUser != null ? string.Format(Loc.T("Alt.Status"), AltAccount.ActiveUser) : "");
        TxtIdentity.Text = $"{TxtUser.Text}\n{string.Format(Loc.T("Config.File"), ConfigService.FilePath)}\n{Log.Path}";
        TxtVersion.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(2)} - .NET {Environment.Version} - {(AppPaths.IsPortable ? "portable" : "%APPDATA%")}";
        TxtStatus.Text = Loc.T("Status.Ready");
        LoadConfigToForm();
        ApplySessionColumns();
        GridLayout.Attach("Sessions", GridSessions);
        GridLayout.Attach("Servers", GridServers);
        GridLayout.Attach("Profiles", GridProfiles);
        GridLayout.Attach("History", GridHistory);

        _timer.Tick += async (_, _) => { if (BtnPause.IsChecked != true && Tabs.SelectedIndex <= 1) await RefreshAsync(); };
        ApplyTimer();
        BtnPause.IsChecked = !Cfg.AutoRefresh;

        Loaded += async (_, _) =>
        {
            _ = CheckForUpdateAsync();
            if (Cfg.Brokers.Count == 0 && Cfg.Servers.Count == 0)
            {
                TxtStatus.Text = Loc.T("Status.NoServers");
                Tabs.SelectedIndex = 4;
            }
            else await RefreshAsync();
        };
        Closing += (_, _) =>
        {
            try
            {
                _refreshCts?.Cancel();
                Cfg.WindowMaximized = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Normal) { Cfg.WindowWidth = Width; Cfg.WindowHeight = Height; }
                ConfigService.Save();
            }
            catch (Exception ex) { Log.Error("Sauvegarde à la fermeture", ex); }
        };
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info == null || !IsLoaded) return;
            if (string.Equals(Cfg.IgnoredUpdateVersion, info.Tag, StringComparison.OrdinalIgnoreCase)) return;

            var current = typeof(App).Assembly.GetName().Version?.ToString(2) ?? "";
            var (update, ignore) = Dialogs.UpdateAvailable(this, current, info.Tag);
            if (ignore)
            {
                Cfg.IgnoredUpdateVersion = info.Tag;
                ConfigService.Save();
            }
            if (update) Tools.OpenFile(info.Url);
        }
        catch (Exception ex) { Log.Error("Vérification de mise à jour", ex); }
    }

    private void ApplyTimer()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(Cfg.RefreshSeconds, 10, 3600));
        if (Cfg.AutoRefresh) _timer.Start();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Pause_Changed(object sender, RoutedEventArgs e)
    {
        if (BtnPause.IsChecked == true) _timer.Stop(); else ApplyTimer();
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _refreshCts = new CancellationTokenSource();
        BtnRefresh.IsEnabled = false;
        TxtStatus.Text = Loc.T("Status.Refreshing");
        try
        {
            var r = await FarmService.RefreshAsync(Cfg, null, _refreshCts.Token);
            ApplyResult(r);
            _lastErrors = r.Errors;
            TxtErrors.Text = r.Errors.Count > 0 ? $"\u26A0 {r.Errors.Count}" : "";
            TxtErrors.ToolTip = r.Errors.Count > 0 ? string.Join("\n", r.Errors.Take(30)) : null;
            TxtStatus.Text = string.Format(Loc.T("Status.Sessions"), r.Sessions.Count(s => s.IsUserSession), r.Servers.Count, r.FailedServers, r.At.ToString("HH:mm:ss"));
        }
        catch (OperationCanceledException) { TxtStatus.Text = Loc.T("Status.Ready"); }
        catch (Exception ex)
        {
            Log.Error("Actualisation", ex);
            TxtStatus.Text = ex.Message;
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void ApplyResult(RefreshResult r)
    {
        var selected = GridSessions.SelectedItems.Cast<RdsSession>().Select(s => s.Key).ToHashSet();
        _sessions.Clear();
        foreach (var s in r.Sessions) _sessions.Add(s);
        if (selected.Count > 0)
            foreach (var s in _sessions.Where(s => selected.Contains(s.Key))) GridSessions.SelectedItems.Add(s);

        var existing = _servers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var s in r.Servers) if (!existing.ContainsKey(s.Name)) _servers.Add(s);
        foreach (var s in _servers.Where(s => !r.Servers.Any(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))).ToList()) _servers.Remove(s);
        foreach (var s in _servers) s.Raise(nameof(RdsServer.InMaintenance));

        var current = CmbHistoryServer.Text;
        CmbHistoryServer.ItemsSource = r.Servers.Select(s => s.Name).ToList();
        CmbHistoryServer.Text = current;
    }

    private DataGridColumn? SessionColumn(string key) => key switch
    {
        "Server" => ColServer, "SessionId" => ColSessionId, "User" => ColUser, "Domain" => ColDomain,
        "State" => ColState, "Client" => ColClient, "ClientIp" => ColClientIp, "Collection" => ColCollection,
        "Type" => ColType, "Logon" => ColLogon, "Connect" => ColConnect, "Disconnect" => ColDisconnect,
        "Idle" => ColIdle, "Resolution" => ColResolution, "Protocol" => ColProtocol, "ClientBuild" => ColClientBuild,
        "Station" => ColStation, _ => null,
    };

    private void ApplySessionColumns()
    {
        var visible = new HashSet<string>(Cfg.SessionColumns ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in AppConfig.AllSessionColumns)
        {
            var col = SessionColumn(key);
            if (col != null) col.Visibility = visible.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void BuildColumnCheckboxes()
    {
        var visible = new HashSet<string>(Cfg.SessionColumns ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        CfgColumnsPanel.Children.Clear();
        foreach (var key in AppConfig.AllSessionColumns)
            CfgColumnsPanel.Children.Add(new CheckBox
            {
                Tag = key,
                Content = Loc.T("Col." + key),
                IsChecked = visible.Contains(key),
                Margin = new Thickness(0, 2, 16, 2),
                MinWidth = 140,
            });
    }

    private List<string> ReadColumnCheckboxes() =>
        CfgColumnsPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();

    private bool SessionFilter(object o)
    {
        if (o is not RdsSession s) return false;
        if (CmbStateFilter.SelectedIndex == 1 && s.State != SessionState.Active) return false;
        if (CmbStateFilter.SelectedIndex == 2 && s.State != SessionState.Disconnected) return false;
        var f = TxtFilter.Text.Trim();
        if (f.Length == 0) return true;
        return Contains(s.UserName, f) || Contains(s.Server, f) || Contains(s.ClientName, f) || Contains(s.ClientIp, f)
            || Contains(s.Collection, f) || Contains(s.Domain, f) || Contains(s.StateText, f) || Contains(s.AppType, f)
            || s.SessionId.ToString() == f;
    }

    private bool ProfileFilter(object o)
    {
        if (o is not ProfileEntry p) return false;
        var f = TxtProfileFilter.Text.Trim();
        return f.Length == 0 || Contains(p.User, f) || Contains(p.Sid, f) || Contains(p.FileName, f) || Contains(p.Kind, f) || Contains(p.Share, f);
    }

    private static bool Contains(string? hay, string needle) => hay != null && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        TxtFilterHint.Visibility = TxtFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _sessionsView.Refresh();
    }

    private void StateFilter_Changed(object sender, SelectionChangedEventArgs e) => _sessionsView?.Refresh();

    private void ProfileFilter_Changed(object sender, TextChangedEventArgs e) => _profilesView.Refresh();

    private void FocusFilter(object sender, ExecutedRoutedEventArgs e) { TxtFilter.Focus(); TxtFilter.SelectAll(); }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        if (Tabs.SelectedIndex == 2 && _profiles.Count == 0 && (Cfg.UpdShares.Count > 0 || Cfg.FsLogixShares.Count > 0)) _ = ScanProfilesAsync();
    }

    private List<RdsSession> SelectedSessions() => GridSessions.SelectedItems.Cast<RdsSession>().ToList();

    private RdsSession? OneSession()
    {
        var s = GridSessions.SelectedItem as RdsSession;
        if (s == null) Dialogs.Info(this, Loc.T("Err.NoSelection"));
        return s;
    }

    private void GridSessions_DoubleClick(object sender, MouseButtonEventArgs e) => SessionDetails_Click(sender, e);

    private void SessionDetails_Click(object sender, RoutedEventArgs e)
    {
        var s = OneSession(); if (s == null) return;
        string D(DateTime? d) => d?.ToString("dd/MM/yyyy HH:mm:ss") ?? "";
        var rows = new List<(string, string)>
        {
            (Loc.T("Col.Server"), s.Server), (Loc.T("Col.SessionId"), s.SessionId.ToString()),
            (Loc.T("Col.User"), s.QualifiedUser), (Loc.T("Col.State"), s.StateText),
            (Loc.T("Col.Collection"), s.Collection), (Loc.T("Col.Type"), s.AppType),
            (Loc.T("Col.Client"), s.ClientName), (Loc.T("Col.ClientIp"), s.ClientIp),
            (Loc.T("Col.Protocol"), s.Protocol), (Loc.T("Col.ClientBuild"), s.ClientBuild.ToString()),
            (Loc.T("Col.Resolution"), s.Resolution), (Loc.T("Col.Station"), s.StationName),
            (Loc.T("Col.Logon"), D(s.LogonTime)), (Loc.T("Col.Connect"), D(s.ConnectTime)),
            (Loc.T("Col.Disconnect"), D(s.DisconnectTime)), (Loc.T("Detail.LastInput"), D(s.LastInputTime)),
            (Loc.T("Col.Idle"), s.IdleText), (Loc.T("Detail.InitialProgram"), s.InitialProgram),
            (Loc.T("Detail.ClientDirectory"), s.ClientDirectory),
            (Loc.T("Detail.Bytes"), $"{s.IncomingBytes:N0} / {s.OutgoingBytes:N0}"),
        };
        Dialogs.KeyValues(this, $"{Loc.T("Detail.Title")} - {s.QualifiedUser} @ {s.Server}#{s.SessionId}", rows);
    }

    private void SessionProcesses_Click(object sender, RoutedEventArgs e)
    {
        var s = OneSession(); if (s == null) return;
        Dialogs.Processes(this, s.Server, s.SessionId, $"{s.QualifiedUser} @ {s.Server}#{s.SessionId}");
    }

    private async void SessionAd_Click(object sender, RoutedEventArgs e)
    {
        var s = OneSession(); if (s == null || !s.IsUserSession) return;
        await Dialogs.AdUser(this, s.Domain, s.UserName);
    }

    private void Shadow_Click(object sender, RoutedEventArgs e) => DoShadow(Cfg.ShadowControl);
    private void ShadowView_Click(object sender, RoutedEventArgs e) => DoShadow(false);

    private void DoShadow(bool control)
    {
        var s = OneSession(); if (s == null) return;
        try { Tools.Shadow(s, control, Cfg.ShadowConsentPrompt); }
        catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private async void SessionMessage_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedSessions().Where(s => s.IsUserSession).ToList();
        if (sel.Count == 0) { Dialogs.Info(this, Loc.T("Err.NoSelection")); return; }
        var msg = Dialogs.Message(this, sel.Count);
        if (msg == null) return;
        var r = await FarmService.ForEachAsync(sel, s => $"{s.QualifiedUser}@{s.Server}", s => WtsApi.SendMessage(s.Server, s.SessionId, msg.Value.title, msg.Value.body), Cfg.Timeout);
        if (r.Failed > 0) Dialogs.Error(this, r.Summary);
        Log.Info($"Message envoyé à {r.Ok} session(s)");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedSessions();
        if (sel.Count == 0) { Dialogs.Info(this, Loc.T("Err.NoSelection")); return; }
        if (!Dialogs.Confirm(this, string.Format(Loc.T("Confirm.Disconnect"), sel.Count))) return;
        var r = await FarmService.ForEachAsync(sel, s => $"{s.QualifiedUser}@{s.Server}", s => WtsApi.Disconnect(s.Server, s.SessionId), Cfg.Timeout + TimeSpan.FromSeconds(10));
        Log.Warn($"Déconnexion : {r.Ok} ok, {r.Failed} échec(s)");
        if (r.Failed > 0) Dialogs.Error(this, r.Summary);
        await RefreshAsync();
    }

    private async void Logoff_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedSessions();
        if (sel.Count == 0) { Dialogs.Info(this, Loc.T("Err.NoSelection")); return; }
        if (!Dialogs.Confirm(this, string.Format(Loc.T("Confirm.Logoff"), sel.Count))) return;
        var r = await FarmService.ForEachAsync(sel, s => $"{s.QualifiedUser}@{s.Server}", s => WtsApi.Logoff(s.Server, s.SessionId), Cfg.Timeout + TimeSpan.FromSeconds(30));
        Log.Warn($"Fermeture de session : {r.Ok} ok, {r.Failed} échec(s)");
        if (r.Failed > 0) Dialogs.Error(this, r.Summary);
        await RefreshAsync();
    }

    private void SessionProfile_Click(object sender, RoutedEventArgs e)
    {
        var s = OneSession(); if (s == null || !s.IsUserSession) return;
        try { Tools.Explorer($@"\\{s.Server}\C$\Users\{s.UserName}"); }
        catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private async void SessionHistory_Click(object sender, RoutedEventArgs e)
    {
        var s = OneSession(); if (s == null || !s.IsUserSession) return;
        TxtHistoryUser.Text = s.UserName;
        CmbHistoryServer.Text = "";
        Tabs.SelectedIndex = 3;
        await ReadHistoryAsync();
    }

    private void PingClient_Click(object sender, RoutedEventArgs e)
    {
        var s = OneSession(); if (s == null) return;
        var host = !string.IsNullOrEmpty(s.ClientIp) ? s.ClientIp : s.ClientName;
        if (!string.IsNullOrEmpty(host)) Tools.Ping(host);
    }

    private void RdpClient_Click(object sender, RoutedEventArgs e)
    {
        var s = OneSession(); if (s == null) return;
        var host = !string.IsNullOrEmpty(s.ClientIp) ? s.ClientIp : s.ClientName;
        if (!string.IsNullOrEmpty(host)) Tools.Rdp(host);
    }

    private void CopyUser_Click(object sender, RoutedEventArgs e) { var s = OneSession(); if (s != null) TrySetClipboard(s.QualifiedUser); }
    private void CopyIp_Click(object sender, RoutedEventArgs e) { var s = OneSession(); if (s != null) TrySetClipboard(s.ClientIp); }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text ?? ""); } catch (Exception ex) { Log.Warn("Presse-papiers : " + ex.Message); }
    }

    private RdsServer? OneServer()
    {
        var s = GridServers.SelectedItem as RdsServer;
        if (s == null) Dialogs.Info(this, Loc.T("Err.NoServer"));
        return s;
    }

    private void GridServers_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var s = GridServers.SelectedItem as RdsServer; if (s == null) return;
        TxtFilter.Text = Net.ShortName(s.Name);
        Tabs.SelectedIndex = 0;
    }

    private void ServerProcesses_Click(object sender, RoutedEventArgs e)
    {
        var s = OneServer(); if (s == null) return;
        Dialogs.Processes(this, s.Name, null, s.Name);
    }

    private async void ServerHistory_Click(object sender, RoutedEventArgs e)
    {
        var s = OneServer(); if (s == null) return;
        TxtHistoryUser.Text = "";
        CmbHistoryServer.Text = s.Name;
        Tabs.SelectedIndex = 3;
        await ReadHistoryAsync();
    }

    private async void ServerMessage_Click(object sender, RoutedEventArgs e)
    {
        var srvs = GridServers.SelectedItems.Cast<RdsServer>().ToList();
        if (srvs.Count == 0) { Dialogs.Info(this, Loc.T("Err.NoServer")); return; }
        var targets = _sessions.Where(x => x.IsUserSession && srvs.Any(v => Net.SameHost(v.Name, x.Server))).ToList();
        if (targets.Count == 0) return;
        var msg = Dialogs.Message(this, targets.Count);
        if (msg == null) return;
        var r = await FarmService.ForEachAsync(targets, s => $"{s.QualifiedUser}@{s.Server}", s => WtsApi.SendMessage(s.Server, s.SessionId, msg.Value.title, msg.Value.body), Cfg.Timeout);
        if (r.Failed > 0) Dialogs.Error(this, r.Summary);
    }

    private async void DrainOn_Click(object sender, RoutedEventArgs e) => await SetDrain(2);
    private async void DrainOff_Click(object sender, RoutedEventArgs e) => await SetDrain(0);

    private async Task SetDrain(int mode)
    {
        var srvs = GridServers.SelectedItems.Cast<RdsServer>().ToList();
        if (srvs.Count == 0) { Dialogs.Info(this, Loc.T("Err.NoServer")); return; }
        var r = await FarmService.ForEachAsync(srvs, s => s.Name, s => WmiService.SetDrainMode(s.Name, mode, Cfg.Timeout), Cfg.Timeout + TimeSpan.FromSeconds(10));
        Log.Warn($"Drain mode={mode} sur {string.Join(",", srvs.Select(s => s.Name))} : {r.Ok} ok, {r.Failed} échec(s)");
        if (r.Failed > 0) Dialogs.Error(this, r.Summary);
        await RefreshAsync();
    }

    private async void ServerDisconnectAll_Click(object sender, RoutedEventArgs e)
    {
        var s = OneServer(); if (s == null) return;
        var targets = _sessions.Where(x => x.IsUserSession && Net.SameHost(x.Server, s.Name)).ToList();
        if (targets.Count == 0 || !Dialogs.Confirm(this, string.Format(Loc.T("Confirm.DisconnectAll"), s.Name))) return;
        var r = await FarmService.ForEachAsync(targets, x => $"{x.QualifiedUser}@{x.Server}", x => WtsApi.Disconnect(x.Server, x.SessionId), Cfg.Timeout + TimeSpan.FromSeconds(10));
        if (r.Failed > 0) Dialogs.Error(this, r.Summary);
        await RefreshAsync();
    }

    private async void ServerLogoffAll_Click(object sender, RoutedEventArgs e)
    {
        var s = OneServer(); if (s == null) return;
        var targets = _sessions.Where(x => x.IsUserSession && Net.SameHost(x.Server, s.Name)).ToList();
        if (targets.Count == 0 || !Dialogs.Confirm(this, string.Format(Loc.T("Confirm.LogoffAll"), s.Name))) return;
        var r = await FarmService.ForEachAsync(targets, x => $"{x.QualifiedUser}@{x.Server}", x => WtsApi.Logoff(x.Server, x.SessionId), Cfg.Timeout + TimeSpan.FromSeconds(30));
        if (r.Failed > 0) Dialogs.Error(this, r.Summary);
        await RefreshAsync();
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e) => await PowerAction(true);
    private async void Shutdown_Click(object sender, RoutedEventArgs e) => await PowerAction(false);

    private async Task PowerAction(bool reboot)
    {
        var s = OneServer(); if (s == null) return;

        if (MessageBox.Show(this, string.Format(Loc.T(reboot ? "Confirm.Reboot" : "Confirm.Shutdown"), s.Name), Loc.T("Confirm.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            await Net.WithTimeout(_ => WmiService.Shutdown(s.Name, reboot, Cfg.Timeout), Cfg.Timeout + TimeSpan.FromSeconds(10));
            Log.Warn($"{(reboot ? "Redémarrage" : "Arrêt")} demandé : {s.Name}");
        }
        catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private void RdpAdmin_Click(object sender, RoutedEventArgs e) { var s = OneServer(); if (s != null) Tools.RdpAdmin(s.Name); }
    private void OpenCShare_Click(object sender, RoutedEventArgs e) { var s = OneServer(); if (s != null) Tools.Explorer($@"\\{s.Name}\C$"); }
    private void CompMgmt_Click(object sender, RoutedEventArgs e) { var s = OneServer(); if (s != null) Tools.CompMgmt(s.Name); }
    private void ServicesMmc_Click(object sender, RoutedEventArgs e) { var s = OneServer(); if (s != null) Tools.Services(s.Name); }

    private async void ScanProfiles_Click(object sender, RoutedEventArgs e) => await ScanProfilesAsync();

    private async Task ScanProfilesAsync()
    {
        if (!BtnScanProfiles.IsEnabled) return;
        BtnScanProfiles.IsEnabled = false;
        TxtProfileStatus.Text = Loc.T("Status.Refreshing");
        try
        {
            var progress = new Progress<string>(s => TxtProfileStatus.Text = s);
            var list = await Task.Run(() => ProfileService.Scan(Cfg.UpdShares, Cfg.FsLogixShares, CancellationToken.None, s => ((IProgress<string>)progress).Report(s)));
            _profiles.Clear();
            foreach (var p in list.OrderBy(p => p.User).ThenBy(p => p.FileName)) _profiles.Add(p);
            var total = list.Sum(p => p.SizeBytes) / (double)(1L << 30);
            TxtProfileStatus.Text = string.Format(Loc.T("Profiles.Status"), list.Count, total, list.Count(p => p.Mounted));
        }
        catch (Exception ex) { TxtProfileStatus.Text = ex.Message; Log.Error("Profils", ex); }
        finally { BtnScanProfiles.IsEnabled = true; }
    }

    private ProfileEntry? OneProfile() => GridProfiles.SelectedItem as ProfileEntry;

    private void ProfileExplore_Click(object sender, RoutedEventArgs e)
    {
        var p = OneProfile(); if (p == null) return;
        try { Tools.Explorer(Path.GetDirectoryName(p.Path) ?? p.Share); } catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private void ProfileCopyPath_Click(object sender, RoutedEventArgs e) { var p = OneProfile(); if (p != null) TrySetClipboard(p.Path); }

    private async void ProfileAd_Click(object sender, RoutedEventArgs e)
    {
        var p = OneProfile(); if (p == null || string.IsNullOrEmpty(p.User)) return;
        var parts = p.User.Split('\\');
        await Dialogs.AdUser(this, parts.Length > 1 ? parts[0] : "", parts[^1]);
    }

    private async void ProfileHistory_Click(object sender, RoutedEventArgs e)
    {
        var p = OneProfile(); if (p == null || string.IsNullOrEmpty(p.User)) return;
        TxtHistoryUser.Text = p.User.Split('\\')[^1];
        CmbHistoryServer.Text = "";
        Tabs.SelectedIndex = 3;
        await ReadHistoryAsync();
    }

    private async void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        var p = OneProfile(); if (p == null || string.IsNullOrEmpty(p.Path)) return;
        if (p.Mounted) { Dialogs.Error(this, Loc.T("Profiles.MountedRefused")); return; }
        if (MessageBox.Show(this, string.Format(Loc.T("Confirm.DeleteProfile"), p.Path), Loc.T("Confirm.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => ProfileService.Delete(p));
            _profiles.Remove(p);
        }
        catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private async void ReadHistory_Click(object sender, RoutedEventArgs e) => await ReadHistoryAsync();
    private async void HistoryUser_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await ReadHistoryAsync(); }

    private async Task ReadHistoryAsync()
    {
        if (!BtnHistory.IsEnabled) return;
        BtnHistory.IsEnabled = false;
        TxtHistoryStatus.Text = Loc.T("Status.Refreshing");
        _history.Clear();
        try
        {
            var user = TxtHistoryUser.Text.Trim();
            var server = CmbHistoryServer.Text.Trim();
            if (!int.TryParse(TxtHistoryDays.Text, out var days) || days < 1) days = Cfg.HistoryDays;
            var servers = string.IsNullOrEmpty(server) ? _servers.Where(s => s.Online).Select(s => s.Name).ToList() : new List<string> { server };
            var all = new List<HistoryEntry>();
            var errors = new List<string>();
            var tasks = servers.Select(async srv =>
            {
                try
                {
                    var list = await Net.WithTimeout(c => HistoryService.Read(srv, days, user, c), TimeSpan.FromSeconds(Math.Max(30, Cfg.TimeoutSeconds * 4)));
                    lock (all) all.AddRange(list);
                }
                catch (Exception ex) { lock (errors) errors.Add($"{srv} : {ex.Message}"); Log.Warn($"Historique {srv} : {ex.Message}"); }
            });
            await Task.WhenAll(tasks);
            foreach (var h in all.OrderByDescending(h => h.Time)) _history.Add(h);
            TxtHistoryStatus.Text = $"{all.Count} - {servers.Count - errors.Count}/{servers.Count} serveur(s)" + (errors.Count > 0 ? $" - {errors.Count} erreur(s)" : "");
            TxtHistoryStatus.ToolTip = errors.Count > 0 ? string.Join("\n", errors) : null;
        }
        catch (Exception ex) { TxtHistoryStatus.Text = ex.Message; }
        finally { BtnHistory.IsEnabled = true; }
    }

    private void Errors_Click(object sender, MouseButtonEventArgs e)
    {
        if (_lastErrors.Count > 0) MessageBox.Show(this, string.Join("\n", _lastErrors), Loc.T("Result.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void LoadConfigToForm()
    {
        var c = Cfg;
        CfgBrokers.Text = AppConfig.JoinList(c.Brokers);
        CfgServers.Text = AppConfig.JoinList(c.Servers);
        CfgExcluded.Text = AppConfig.JoinList(c.ExcludedServers);
        CfgDomains.Text = string.Join(";", c.DomainFilter);
        CfgLdap.Text = c.LdapPath;
        CfgUpd.Text = AppConfig.JoinList(c.UpdShares);
        CfgFsLogix.Text = AppConfig.JoinList(c.FsLogixShares);
        CfgRefresh.Text = c.RefreshSeconds.ToString();
        CfgTimeout.Text = c.TimeoutSeconds.ToString();
        CfgHistoryDays.Text = c.HistoryDays.ToString();
        TxtHistoryDays.Text = c.HistoryDays.ToString();
        CfgLanguage.SelectedValue = Loc.Normalize(c.Language);
        CfgAutoRefresh.IsChecked = c.AutoRefresh;
        CfgConfirm.IsChecked = c.ConfirmActions;
        CfgSystem.IsChecked = c.ShowSystemSessions;
        CfgPerf.IsChecked = c.CollectPerformance;
        CfgShadowControl.IsChecked = c.ShadowControl;
        CfgShadowConsent.IsChecked = c.ShadowConsentPrompt;
        CfgAltEnabled.IsChecked = c.UseAltAccount;
        CfgAltUser.Text = c.AltAccountUser;
        CfgAltPassword.Password = "";
        CfgAltSavePwd.IsChecked = !string.IsNullOrEmpty(c.AltAccountPassword);
        CfgAltNetOnly.IsChecked = c.AltAccountNetOnly;
        BuildColumnCheckboxes();
        TxtConfigFile.Text = string.Format(Loc.T("Config.File"), ConfigService.FilePath);
    }

    private async void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        var c = Cfg;
        if (!await CheckAltAccountAsync()) return;
        c.Brokers = AppConfig.SplitList(CfgBrokers.Text);
        c.Servers = AppConfig.SplitList(CfgServers.Text);
        c.ExcludedServers = AppConfig.SplitList(CfgExcluded.Text);
        c.DomainFilter = AppConfig.SplitList(CfgDomains.Text);
        c.LdapPath = CfgLdap.Text.Trim();
        c.UpdShares = AppConfig.SplitList(CfgUpd.Text);
        c.FsLogixShares = AppConfig.SplitList(CfgFsLogix.Text);
        if (int.TryParse(CfgRefresh.Text, out var r)) c.RefreshSeconds = Math.Clamp(r, 10, 3600);
        if (int.TryParse(CfgTimeout.Text, out var t)) c.TimeoutSeconds = Math.Clamp(t, 2, 120);
        if (int.TryParse(CfgHistoryDays.Text, out var h)) c.HistoryDays = Math.Clamp(h, 1, 365);
        var lang = CfgLanguage.SelectedValue as string ?? Loc.Normalize(c.Language);
        var langChanged = lang != c.Language;
        c.Language = lang;
        c.AutoRefresh = CfgAutoRefresh.IsChecked == true;
        c.ConfirmActions = CfgConfirm.IsChecked == true;
        c.ShowSystemSessions = CfgSystem.IsChecked == true;
        c.CollectPerformance = CfgPerf.IsChecked == true;
        c.ShadowControl = CfgShadowControl.IsChecked == true;
        c.ShadowConsentPrompt = CfgShadowConsent.IsChecked == true;
        ReadAltAccountForm(c);
        c.SessionColumns = ReadColumnCheckboxes();
        try
        {
            ConfigService.Save();
            if (langChanged) Loc.I.Language = lang;
            LoadConfigToForm();
            ApplySessionColumns();
            ApplyTimer();
            BtnPause.IsChecked = !c.AutoRefresh;
            TxtStatus.Text = Loc.T("Config.Saved");
            if (c.Brokers.Count > 0 || c.Servers.Count > 0) { Tabs.SelectedIndex = 0; await RefreshAsync(); }
        }
        catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "Dahu.config.json" };
        if (dlg.ShowDialog(this) != true) return;
        try { ConfigService.Export(dlg.FileName); } catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try { ConfigService.Import(dlg.FileName); Loc.I.Language = Cfg.Language; LoadConfigToForm(); ApplySessionColumns(); GridLayout.RestoreAll(); ApplyTimer(); }
        catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private async Task<bool> CheckAltAccountAsync()
    {
        if (CfgAltEnabled.IsChecked != true) return true;
        var user = CfgAltUser.Text.Trim();
        if (user.Length == 0) { Dialogs.Error(this, Loc.T("Config.AltUser")); return false; }
        var pwd = CfgAltPassword.Password.Length > 0 ? CfgAltPassword.Password : AltAccount.Unprotect(Cfg.AltAccountPassword);
        pwd ??= Dialogs.AskPassword(this, user);
        if (pwd == null) return false;

        TxtStatus.Text = string.Format(Loc.T("Alt.Testing"), user);
        IsEnabled = false;
        AltAccount.TestResult res;
        try
        {
            var probe = new AppConfig
            {
                Servers = AppConfig.SplitList(CfgServers.Text), Brokers = AppConfig.SplitList(CfgBrokers.Text),
                TimeoutSeconds = int.TryParse(CfgTimeout.Text, out var t) ? Math.Clamp(t, 2, 120) : Cfg.TimeoutSeconds,
            };
            res = await AltAccount.TestAsync(user, pwd, probe);
        }
        catch (Exception ex) { res = new AltAccount.TestResult(false, ex.Message); }
        finally { IsEnabled = true; TxtStatus.Text = Loc.T("Status.Ready"); }

        if (res.Ok) { Dialogs.Info(this, string.Format(Loc.T("Alt.TestOkMsg"), user, res.Report)); return true; }
        return MessageBox.Show(this, string.Format(Loc.T("Alt.SaveAnyway"), user, res.Report), Loc.T("Confirm.Title"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void ReadAltAccountForm(AppConfig c)
    {
        c.UseAltAccount = CfgAltEnabled.IsChecked == true;
        c.AltAccountUser = CfgAltUser.Text.Trim();
        c.AltAccountNetOnly = CfgAltNetOnly.IsChecked == true;
        if (CfgAltSavePwd.IsChecked != true) c.AltAccountPassword = "";
        else if (CfgAltPassword.Password.Length > 0) c.AltAccountPassword = AltAccount.Protect(CfgAltPassword.Password);
    }

    private void RunAs_Click(object sender, RoutedEventArgs e)
    {
        var user = CfgAltUser.Text.Trim();
        if (user.Length == 0) { Dialogs.Error(this, Loc.T("Config.AltUser")); return; }
        var pwd = CfgAltPassword.Password.Length > 0 ? CfgAltPassword.Password : AltAccount.Unprotect(Cfg.AltAccountPassword);
        pwd ??= Dialogs.AskPassword(this, user);
        if (pwd == null) return;
        try
        {
            ReadAltAccountForm(Cfg);
            ConfigService.Save();
            App.ReleaseInstanceMutex();
            try { AltAccount.Launch(user, pwd, CfgAltNetOnly.IsChecked == true); }
            catch { App.AcquireInstanceMutex(); throw; }
            Application.Current.Shutdown();
        }
        catch (Exception ex) { Dialogs.Error(this, string.Format(Loc.T("Alt.Failed"), user, ex.Message)); }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { if (File.Exists(Log.Path)) Tools.OpenFile(Log.Path); else Tools.Explorer(AppPaths.DataDir); }
        catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try { Tools.Explorer(AppPaths.DataDir); } catch (Exception ex) { Dialogs.Error(this, ex.Message); }
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Tools.OpenFile(e.Uri.AbsoluteUri); } catch (Exception ex) { Dialogs.Error(this, ex.Message); }
        e.Handled = true;
    }
}
