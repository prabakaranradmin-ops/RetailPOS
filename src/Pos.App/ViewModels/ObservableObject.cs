using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pos.App.ViewModels;

/// <summary>Minimal INotifyPropertyChanged base. Not worth a framework dependency.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    private Dictionary<string, string[]>? _follows;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        if (propertyName is not null && _follows is not null && _follows.TryGetValue(propertyName, out var dependents))
        {
            foreach (var dependent in dependents)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(dependent));
        }
    }

    /// <summary>
    /// Raises <paramref name="dependents"/> whenever <paramref name="source"/> is: properties worked
    /// out from it, which would otherwise need raising at every one of its call sites.
    /// </summary>
    protected void Follow(string source, params string[] dependents) =>
        (_follows ??= new Dictionary<string, string[]>(StringComparer.Ordinal))[source] = dependents;

    /// <summary>Raises change notification for every bound property on this object.</summary>
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        Raise(propertyName);
        return true;
    }
}
