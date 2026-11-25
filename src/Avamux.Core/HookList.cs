using Avamux.Core.Logging;

namespace Avamux.Core;

/// <summary>
/// Things plugins add to one of avamux's services (a way to read start scripts, a program to recognise …). Disposing
/// what <see cref="Add"/> returns takes it out again, which a plugin does when it is switched off.
/// </summary>
public sealed class HookList<T> where T : class
{
    private readonly object _gate = new();
    private T[] _items = [];

    public IDisposable Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate) _items = [.. _items, item];
        return new Removal(() => { lock (_gate) _items = _items.Where(i => !ReferenceEquals(i, item)).ToArray(); });
    }

    /// <summary>What has been added, oldest first.</summary>
    public IReadOnlyList<T> Items => Volatile.Read(ref _items);

    /// <summary>Calls each one, logging (not throwing) what one of them throws, so a broken plugin cannot break the caller.</summary>
    public IEnumerable<TResult> Each<TResult>(Func<T, TResult> call, string area)
    {
        foreach (var item in Items)
        {
            TResult result;
            try { result = call(item); }
            catch (Exception ex) { Log.Warn(area, $"A plugin's hook failed: {ex.Message}"); continue; }
            yield return result;
        }
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
