using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
        Setup(grid);
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
        foreach (var (name, grid) in Grids) { Restore(name, grid); Freeze(grid); }
    }

    public static void Setup(DataGrid grid)
    {
        var defaults = grid.Columns.ToDictionary(c => c, c => c.Width);
        AttachResize(grid);
        grid.Loaded += (_, _) => FreezeLater(grid);
        grid.PreviewMouseRightButtonUp += (_, e) =>
        {
            var col = HeaderColumn(grid, e.OriginalSource as DependencyObject, out var onHeader);
            if (!onHeader) return;
            e.Handled = true;
            if (col == null || !defaults.TryGetValue(col, out var width)) return;
            col.Width = width;
            if (width.IsStar) FreezeLater(grid);
        };
        grid.ContextMenuOpening += (_, e) =>
        {
            HeaderColumn(grid, e.OriginalSource as DependencyObject, out var onHeader);
            if (onHeader) e.Handled = true;
        };
    }

    private static void FreezeLater(DataGrid grid)
        => grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Freeze(grid));

    private static DataGridColumn? HeaderColumn(DataGrid grid, DependencyObject? source, out bool onHeader)
    {
        Thumb? thumb = null;
        for (var d = source; d != null && d != grid; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is Thumb t) thumb ??= t;
            if (d is DataGridColumnHeader header)
            {
                onHeader = true;
                return thumb != null ? ResizeTarget(grid, thumb) : header.Column;
            }
        }
        onHeader = false;
        return null;
    }

    private static void AttachResize(DataGrid grid)
    {
        DataGridColumn? target = null;
        double startWidth = 0, startX = 0;

        grid.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, e) =>
        {
            target = ResizeTarget(grid, e.OriginalSource as Thumb);
            if (target == null) return;
            startWidth = target.ActualWidth;
            startX = Mouse.GetPosition(grid).X;
            e.Handled = true;
        }));
        grid.AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler((_, e) =>
        {
            if (target == null) return;
            var w = Math.Clamp(startWidth + Mouse.GetPosition(grid).X - startX, target.MinWidth, target.MaxWidth);
            target.Width = new DataGridLength(w, DataGridLengthUnitType.Pixel);
            e.Handled = true;
        }));
        grid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            if (target == null) return;
            target = null;
            e.Handled = true;
        }));
    }

    private static DataGridColumn? ResizeTarget(DataGrid grid, Thumb? thumb)
    {
        if (thumb?.TemplatedParent is not DataGridColumnHeader header || header.Column == null) return null;
        if (!grid.CanUserResizeColumns) return null;

        DataGridColumn? col = thumb.Name switch
        {
            "RightGripper" => header.Column,
            "LeftGripper" => grid.Columns
                .Where(c => c.Visibility == System.Windows.Visibility.Visible && c.DisplayIndex < header.Column.DisplayIndex)
                .OrderByDescending(c => c.DisplayIndex).FirstOrDefault(),
            _ => null,
        };
        return col is { CanUserResize: true } ? col : null;
    }

    private static void Freeze(DataGrid grid)
    {
        if (!grid.IsLoaded) return;
        foreach (var col in grid.Columns)
        {
            if (!col.Width.IsStar) continue;
            var w = Math.Max(col.ActualWidth, Math.Max(col.MinWidth, 150));
            col.Width = new DataGridLength(w, DataGridLengthUnitType.Pixel);
        }
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
