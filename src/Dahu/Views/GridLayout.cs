using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using Dahu.Core;

namespace Dahu.Views;

public static class GridLayout
{
    private static readonly DispatcherTimer SaveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private static readonly List<(string Name, DataGrid Grid)> Grids = new();
    private static bool _restoring;

    static GridLayout()
    {
        SaveTimer.Tick += (_, _) =>
        {
            SaveTimer.Stop();
            try { CaptureAll(); ConfigService.Save(); }
            catch (Exception ex) { Log.Error("Sauvegarde de la mise en page des colonnes", ex); }
        };
    }

    public static void Attach(string name, DataGrid grid)
    {
        Grids.Add((name, grid));
        Restore(name, grid);

        var widthProp = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
        var indexProp = DependencyPropertyDescriptor.FromProperty(DataGridColumn.DisplayIndexProperty, typeof(DataGridColumn));
        foreach (var col in grid.Columns)
        {
            widthProp.AddValueChanged(col, OnChanged);
            indexProp.AddValueChanged(col, OnChanged);
        }
    }

    public static void RestoreAll()
    {
        foreach (var (name, grid) in Grids) Restore(name, grid);
    }

    private static void OnChanged(object? sender, EventArgs e)
    {
        if (_restoring) return;
        SaveTimer.Stop();
        SaveTimer.Start();
    }

    private static void Restore(string name, DataGrid grid)
    {
        var layouts = ConfigService.Current.ColumnLayouts;
        if (layouts == null || !layouts.TryGetValue(name, out var saved) || saved == null || saved.Count == 0) return;

        _restoring = true;
        try
        {
            var n = grid.Columns.Count;
            foreach (var c in saved)
            {
                if (c.Index < 0 || c.Index >= n) continue;
                if (c.Width > 0)
                    grid.Columns[c.Index].Width = c.Star
                        ? new DataGridLength(c.Width, DataGridLengthUnitType.Star)
                        : new DataGridLength(c.Width, DataGridLengthUnitType.Pixel);
            }

            var order = saved.Where(c => c.Index >= 0 && c.Index < n).ToList();
            var valid = order.Count == n
                && order.Select(c => c.Index).Distinct().Count() == n
                && order.Select(c => c.DisplayIndex).OrderBy(i => i).SequenceEqual(Enumerable.Range(0, n));
            if (valid)
            {
                foreach (var c in order.OrderBy(c => c.DisplayIndex))
                    grid.Columns[c.Index].DisplayIndex = c.DisplayIndex;
            }
        }
        catch (Exception ex) { Log.Error($"Restauration de la mise en page de la grille {name}", ex); }
        finally { _restoring = false; }
    }

    private static void CaptureAll()
    {
        var cfg = ConfigService.Current;
        cfg.ColumnLayouts ??= new Dictionary<string, List<ColumnLayout>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, grid) in Grids)
        {
            var list = new List<ColumnLayout>();
            for (int i = 0; i < grid.Columns.Count; i++)
            {
                var col = grid.Columns[i];
                var w = col.Width;
                list.Add(new ColumnLayout
                {
                    Index = i,
                    Width = w.IsStar ? w.Value : (w.IsAbsolute ? w.Value : col.ActualWidth),
                    Star = w.IsStar,
                    DisplayIndex = col.DisplayIndex,
                });
            }
            cfg.ColumnLayouts[name] = list;
        }
    }
}
