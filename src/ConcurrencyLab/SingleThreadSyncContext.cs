using System.Collections.Concurrent;

namespace ConcurrencyLab;

/// <summary>
/// A tiny single-threaded SynchronizationContext, mimicking what a UI thread (WPF/WinForms) or
/// classic ASP.NET gives you: "continuations must run back on THIS one thread".
/// Console apps and ASP.NET Core have no SynchronizationContext, so we build one to reproduce the classic deadlock safely.
/// </summary>
public sealed class SingleThreadSyncContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public SingleThreadSyncContext()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "UI-like thread" };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // Context already shut down; drop late continuations.
        }
    }

    private void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }

    /// <summary>Runs <paramref name="func"/> on the context thread (like a button click handler).</summary>
    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
