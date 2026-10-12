namespace MemeManager.Infrastructure;

// TODO AsyncDebounce类缺单元测试
public interface IDebouncer<T>
{
    public CancellationToken? Token { get; }
    public void Trigger(T data);
    public void CancelPending();
}




public class Debouncer<T>(TimeSpan delay, Action<T> action) : AsyncDebouncer<T>(delay, t =>
    {
        action(t);
        return Task.CompletedTask;
    })
{ }


public class Debouncer : Debouncer<object?>
{
    public Debouncer(TimeSpan delay, Action action)
        : base(delay, _ =>
        {
            action();
        })
    { }

    
    public void Trigger()
    {
        Trigger(null);
    }
}




public class AsyncDebouncer<T> : IDebouncer<T>
{
    private readonly TimeSpan _delay;
    
    private readonly Func<T, CancellationToken, Task> _asyncAction;

    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;

    public CancellationToken? Token => _cts?.Token;

    
    public AsyncDebouncer(TimeSpan delay, Func<T, CancellationToken, Task> asyncAction)
    {
        _delay = delay;
        _asyncAction = asyncAction;
    }

    
    public AsyncDebouncer(TimeSpan delay, Func<T, Task> asyncAction)
        : this(delay, (data, _) => asyncAction(data)) 
    {
    }

    public void Trigger(T data)
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _ = ExecuteAsync(data, token);
        }
    }

    private async Task ExecuteAsync(T data, CancellationToken token)
    {
        try
        {
            await Task.Delay(_delay, token);

            await _asyncAction(data, token);
        }
        catch (OperationCanceledException)
        { }
        catch (Exception ex)
        {
            Logger.Log($"防抖业务执行异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 默认只能取消未执行的 Task；若触发的业务自身支持且传入了 CancellationToken，正在执行的任务也会被一起取消。
    /// </summary>
    public void CancelPending()
    {
        lock (_lock)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }
    }
}


public class AsyncDebouncer : AsyncDebouncer<object?>
{
    public AsyncDebouncer(TimeSpan delay, Func<Task> asyncAction)
        : base(delay, (_) => asyncAction())
    {
    }

    
    public AsyncDebouncer(TimeSpan delay, Func<CancellationToken, Task> asyncAction)
        : base(delay, (_, token) => asyncAction(token))
    {
    }

    
    public void Trigger()
    {
        Trigger(null);
    }
}
