using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Floresta.Services;

internal sealed class FlorestaBackgroundTasks
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _tasks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private long _nextId;
    private bool _accepting = true;

    public Task Run(Func<CancellationToken, Task> action)
    {
        var key = $"job:{Interlocked.Increment(ref _nextId)}";
        return TryRun(key, action) ?? Task.CompletedTask;
    }

    public Task TryRun(
        string key,
        Func<CancellationToken, Task> action,
        Action onStarting = null,
        Action onCompleted = null)
    {
        lock (_gate)
        {
            if (!_accepting || _tasks.ContainsKey(key))
                return null;

            onStarting?.Invoke();
            var task = Task.Run(() => action(_stopping.Token), CancellationToken.None);
            _tasks.Add(key, task);
            _ = task.ContinueWith(
                completed => Remove(key, completed, onCompleted),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] tasks;
        lock (_gate)
        {
            _accepting = false;
            tasks = _tasks.Values.ToArray();
        }

        _stopping.Cancel();
        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Remove(string key, Task completed, Action onCompleted)
    {
        var removed = false;
        lock (_gate)
        {
            if (_tasks.TryGetValue(key, out var current) && ReferenceEquals(current, completed))
                removed = _tasks.Remove(key);
        }

        if (removed)
            onCompleted?.Invoke();
    }
}
