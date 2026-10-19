using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Dahu.Core;
using Dahu.Models;
using Dahu.Services;

namespace Dahu.Views;

internal static class Dialogs
{
    private static Window Base(Window owner, string title, double w, double h)
    {
        var win = new Window
        {
            Title = title, Owner = owner, Width = w, Height = h,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5,
            ShowInTaskbar = false, Icon = owner.Icon,
        };
        win.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
        win.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        Theme.Attach(win);
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        return win;
    }

    public static bool Confirm(Window owner, string text)
    {
        if (!ConfigService.Current.ConfirmActions) return true;
        return MessageBox.Show(owner, text, Loc.T("Confirm.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public static void Error(Window owner, string text)
        => MessageBox.Show(owner, text, Loc.T("Result.Error"), MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Info(Window owner, string text)
        => MessageBox.Show(owner, text, "Dahu", MessageBoxButton.OK, MessageBoxImage.Information);

    public static (string title, string body)? Message(Window owner, int targetCount)
    {
        var win = Base(owner, string.Format(Loc.T("Msg.SendTo"), targetCount), 520, 340);
        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lt = new TextBlock { Text = Loc.T("Msg.Title") };
        var title = new TextBox { Text = Loc.T("Msg.DefaultTitle"), Margin = new Thickness(0, 2, 0, 8) };
        var lb = new TextBlock { Text = Loc.T("Msg.Body") };
        var body = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 2, 0, 10), VerticalContentAlignment = VerticalAlignment.Top };
        Grid.SetRow(lt, 0); Grid.SetRow(title, 1); Grid.SetRow(lb, 2); Grid.SetRow(body, 3);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttons, 4);
        var ok = new Button { Content = Loc.T("Btn.Send"), Style = (Style)owner.FindResource("PrimaryButton"), IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = Loc.T("Btn.Cancel"), IsCancel = true, MinWidth = 90 };
        bool accepted = false;
        ok.Click += (_, _) => { if (body.Text.Trim().Length > 0) { accepted = true; win.Close(); } };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        grid.Children.Add(lt); grid.Children.Add(title); grid.Children.Add(lb); grid.Children.Add(body); grid.Children.Add(buttons);
        win.Content = grid;
        win.Loaded += (_, _) => body.Focus();
        win.ShowDialog();
        return accepted ? (title.Text.Trim(), body.Text.Trim()) : null;
    }

    public static Window KeyValues(Window owner, string title, IEnumerable<(string key, string value)> rows, IEnumerable<(string label, Func<Task> onClick)>? actions = null, double w = 620, double h = 560)
    {
        var win = Base(owner, title, w, h);
        var dock = new DockPanel { Margin = new Thickness(12) };
        if (actions != null)
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            foreach (var (label, onClick) in actions)
            {
                var b = new Button { Content = label };
                b.Click += async (_, _) =>
                {
                    b.IsEnabled = false;
                    try { await onClick(); }
                    catch (Exception ex) { Error(win, ex.Message); }
                    finally { b.IsEnabled = true; }
                };
                bar.Children.Add(b);
            }
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
        }
        var grid = new DataGrid
        {
            ItemsSource = rows.Select(r => new KeyValue(r.key, r.value)).ToList(),
            HeadersVisibility = DataGridHeadersVisibility.None,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.Cell,
        };
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding("Key"), Width = 190, FontWeight = FontWeights.SemiBold });
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding("Value"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        dock.Children.Add(grid);
        win.Content = dock;
        win.Show();
        return win;
    }

    private sealed record KeyValue(string Key, string Value);

    public static void Processes(Window owner, string server, int? sessionId, string label)
    {
        var win = Base(owner, $"{Loc.T("Proc.Title")} - {label}", 900, 600);
        win.ShowInTaskbar = true;
        var dock = new DockPanel { Margin = new Thickness(12) };
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var refresh = new Button { Content = Loc.T("Btn.Refresh"), Style = (Style)owner.FindResource("PrimaryButton") };
        var kill = new Button { Content = Loc.T("Btn.Kill") };
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        var filter = new TextBox { Width = 220, Margin = new Thickness(12, 0, 0, 0), ToolTip = Loc.T("Proc.Filter") };
        bar.Children.Add(refresh); bar.Children.Add(kill); bar.Children.Add(filter); bar.Children.Add(status);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);

        var grid = new DataGrid();
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Col.Pid"), Binding = new Binding("Pid"), Width = 65 });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Col.Process"), Binding = new Binding("Name"), Width = 200 });
        if (sessionId == null) grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Col.SessionId"), Binding = new Binding("SessionId"), Width = 50 });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Col.User"), Binding = new Binding("Owner"), Width = 170 });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Col.WorkingSet"), Binding = new Binding("WorkingSetMb"), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Col.CpuTime"), Binding = new Binding("CpuTimeText"), Width = 80, SortMemberPath = "CpuTime" });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Col.Started"), Binding = new Binding("Started") { StringFormat = "dd/MM HH:mm:ss" }, Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Col.Path"), Binding = new Binding("Path"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        GridLayout.Setup(grid);
        dock.Children.Add(grid);
        win.Content = dock;

        List<RdsProcess> all = new();
        void Apply()
        {
            var f = filter.Text.Trim();
            var view = string.IsNullOrEmpty(f) ? all : all.Where(p => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Owner.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Path.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            grid.ItemsSource = view.OrderByDescending(p => p.WorkingSetMb).ToList();
            status.Text = $"{view.Count} / {all.Count}";
        }
        async Task Load()
        {
            refresh.IsEnabled = false; status.Text = Loc.T("Status.Refreshing");
            try
            {
                var t = ConfigService.Current.Timeout;
                all = await Net.WithTimeout(c => WmiService.GetProcesses(server, sessionId, t, c), t + TimeSpan.FromSeconds(10));
                Apply();
            }
            catch (Exception ex) { status.Text = ex.Message; Log.Error($"Processus {server}", ex); }
            finally { refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await Load();
        filter.TextChanged += (_, _) => Apply();
        kill.Click += async (_, _) =>
        {
            var sel = grid.SelectedItems.Cast<RdsProcess>().ToList();
            if (sel.Count == 0) return;
            if (!Confirm(win, string.Join("\n", sel.Select(p => string.Format(Loc.T("Confirm.Kill"), p.Name, p.Pid))))) return;
            var r = await FarmService.ForEachAsync(sel, p => $"{p.Name} ({p.Pid})", p => WmiService.KillProcess(server, p.Pid, ConfigService.Current.Timeout), ConfigService.Current.Timeout + TimeSpan.FromSeconds(5));
            if (r.Failed > 0) Error(win, r.Summary);
            await Load();
        };
        win.Loaded += async (_, _) => await Load();
        win.Show();
    }

    public static async Task AdUser(Window owner, string domain, string sam)
    {
        AdUserInfo? u;
        try
        {
            u = await Task.Run(() => AdService.Find(domain, sam));
        }
        catch (Exception ex) { Error(owner, ex.Message); return; }
        if (u == null) { Info(owner, Loc.T("Ad.NotFound")); return; }

        string D(DateTime? d) => d?.ToString("dd/MM/yyyy HH:mm") ?? "";
        string B(bool b) => Loc.T(b ? "Common.Yes" : "Common.No");
        var rows = new List<(string, string)>
        {
            ("sAMAccountName", u.SamAccountName),
            (Loc.T("Ad.Field.DisplayName"), u.DisplayName),
            (Loc.T("Ad.Field.Email"), u.Email),
            (Loc.T("Ad.Field.Description"), u.Description),
            (Loc.T("Ad.Field.Enabled"), B(u.Enabled)),
            (Loc.T("Ad.Field.LockedOut"), B(u.LockedOut)),
            (Loc.T("Ad.Field.LastLogon"), D(u.LastLogon)),
            (Loc.T("Ad.Field.PwdLastSet"), D(u.LastPasswordSet)),
            (Loc.T("Ad.Field.MustChangePwd"), B(u.MustChangePassword)),
            (Loc.T("Ad.Field.PwdNeverExpires"), B(u.PasswordNeverExpires)),
            (Loc.T("Ad.Field.AccountExpires"), D(u.AccountExpires)),
            (Loc.T("Ad.Field.Department"), u.Department),
            (Loc.T("Ad.Field.Title"), u.Title),
            (Loc.T("Ad.Field.Company"), u.Company),
            (Loc.T("Ad.Field.Office"), u.Office),
            (Loc.T("Ad.Field.Phone"), u.Phone),
            (Loc.T("Ad.Field.Mobile"), u.Mobile),
            (Loc.T("Ad.Field.Manager"), u.Manager),
            (Loc.T("Ad.Field.HomeDirectory"), u.HomeDirectory),
            (Loc.T("Ad.Field.ProfilePath"), u.ProfilePath),
            (Loc.T("Ad.Field.TsProfilePath"), u.TsProfilePath),
            (Loc.T("Ad.Field.TsHomeDirectory"), u.TsHomeDirectory),
            (Loc.T("Ad.Field.Created"), D(u.WhenCreated)),
            ("SID", u.Sid),
            ("DN", u.DistinguishedName),
            (Loc.T("Ad.Field.Groups"), string.Join(", ", u.Groups)),
        };

        Window? win = null;
        var actions = new List<(string, Func<Task>)>
        {
            (u.Enabled ? Loc.T("Ad.Disable") : Loc.T("Ad.Enable"), async () =>
            {
                if (!Confirm(win!, string.Format(Loc.T(u.Enabled ? "Confirm.AdDisable" : "Confirm.AdEnable"), sam))) return;
                await Task.Run(() => AdService.SetEnabled(domain, sam, !u.Enabled));
                win!.Close(); _ = AdUser(owner, domain, sam);
            }),
            (Loc.T("Ad.Unlock"), async () =>
            {
                if (!Confirm(win!, string.Format(Loc.T("Confirm.AdUnlock"), sam))) return;
                await Task.Run(() => AdService.Unlock(domain, sam));
                win!.Close(); _ = AdUser(owner, domain, sam);
            }),
            (Loc.T("Ad.ForcePwd"), async () =>
            {
                if (!Confirm(win!, string.Format(Loc.T("Confirm.AdPwdChange"), sam))) return;
                await Task.Run(() => AdService.ExpirePassword(domain, sam));
                win!.Close(); _ = AdUser(owner, domain, sam);
            }),
        };
        win = KeyValues(owner, $"{Loc.T("Ad.Title")} - {domain}\\{sam}", rows, actions, 700, 640);
    }

    public static (bool update, bool ignore) UpdateAvailable(Window owner, string current, string latest)
    {
        var win = Base(owner, Loc.T("Update.Title"), 440, 200);
        win.ResizeMode = ResizeMode.NoResize;
        win.SizeToContent = SizeToContent.Height;
        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock { Text = string.Format(Loc.T("Update.Text"), latest, current), TextWrapping = TextWrapping.Wrap });
        var ignore = new CheckBox { Content = Loc.T("Update.Ignore"), Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = Loc.T("Update.Yes"), Style = (Style)owner.FindResource("PrimaryButton"), IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = Loc.T("Update.No"), IsCancel = true, MinWidth = 90 };
        bool update = false;
        ok.Click += (_, _) => { update = true; win.Close(); };
        no.Click += (_, _) => win.Close();
        buttons.Children.Add(ok); buttons.Children.Add(no);
        sp.Children.Add(ignore); sp.Children.Add(buttons);
        win.Content = sp;
        win.ShowDialog();
        return (update, ignore.IsChecked == true);
    }

    public static string? AskPassword(Window? owner, string user)
    {
        var win = new Window
        {
            Title = Loc.T("Alt.PromptTitle"), Width = 420, Height = 190, ResizeMode = ResizeMode.NoResize,
            Owner = owner, ShowInTaskbar = owner == null, Topmost = owner == null,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5,
        };
        try { win.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/dahu.ico")); } catch { }
        win.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
        win.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        Theme.Attach(win);
        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock { Text = string.Format(Loc.T("Alt.PromptText"), user), TextWrapping = TextWrapping.Wrap });
        var pwd = new PasswordBox { Margin = new Thickness(0, 6, 0, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = Loc.T("Btn.OK"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Loc.T("Btn.Cancel"), IsCancel = true, MinWidth = 90 };
        try { ok.Style = (Style)Application.Current.FindResource("PrimaryButton"); } catch { }
        string? result = null;
        ok.Click += (_, _) => { result = pwd.Password; win.DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        sp.Children.Add(pwd); sp.Children.Add(buttons);
        win.Content = sp;
        win.Loaded += (_, _) => { win.Activate(); pwd.Focus(); };
        win.ShowDialog();
        return result;
    }
}
