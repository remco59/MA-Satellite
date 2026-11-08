using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Presentation;

/// <summary>Where the library window currently is: a top-level tab, optionally an opened item, and the search text.</summary>
public sealed record NavState(string Tab, MaItem? Item = null, string Query = "");

/// <summary>Browser-style back/forward history. Navigating to a new place discards the forward stack.</summary>
public sealed class NavigationHistory<T>
{
    private readonly Stack<T> _back = new();
    private readonly Stack<T> _forward = new();

    public NavigationHistory(T initial) => Current = initial;

    public T Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Moves to <paramref name="next"/>. Returns false (and changes nothing) if it is the current place.</summary>
    public bool Navigate(T next)
    {
        if (EqualityComparer<T>.Default.Equals(next, Current))
        {
            return false;
        }

        _back.Push(Current);
        _forward.Clear();
        Current = next;
        return true;
    }

    /// <summary>Changes the current place without adding a history entry (e.g. a forced reload).</summary>
    public void Replace(T state) => Current = state;

    public T GoBack()
    {
        if (_back.Count == 0)
        {
            return Current;
        }

        _forward.Push(Current);
        Current = _back.Pop();
        return Current;
    }

    public T GoForward()
    {
        if (_forward.Count == 0)
        {
            return Current;
        }

        _back.Push(Current);
        Current = _forward.Pop();
        return Current;
    }
}
