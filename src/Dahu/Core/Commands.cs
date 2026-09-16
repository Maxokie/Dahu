using System.Windows.Input;

namespace Dahu.Core;

public static class Commands
{
    public static readonly RoutedUICommand Refresh = new("Refresh", "Refresh", typeof(Commands));
    public static readonly RoutedUICommand FocusFilter = new("FocusFilter", "FocusFilter", typeof(Commands));
}
