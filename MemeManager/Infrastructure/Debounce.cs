namespace MemeManager.Infrastructure;

// TODO AsyncDebounce类缺单元测试
public interface IDebouncer<T>
{
    public CancellationToken? Token { get; }
    public void Trigger(T data);
    public void CancelPending();
}

/// <summary>
/// 同步防抖器：把同步委托包成 <see cref="AsyncDebouncer{T}"/> 所需的异步委托。
/// </summary>
public class Debouncer<T>(TimeSpan delay, Action<T> action) : AsyncDebouncer<T>(delay, t =>
    {
        action(t);
        return Task.CompletedTask;
    })
{ }

/// <summary>无参数的同步防抖器（业务逻辑不支持取消）。</summary>
public class Debouncer : Debouncer<object?>
{
    public Debouncer(TimeSpan delay, Action action)
        : base(delay, _ =>
        {
            action();
        })
    { }

    /// <summary>触发防抖（无需传参）。</summary>
    public void Trigger()
    {
        Trigger(null);
    }
}

/// <summary>
/// 异步防抖器：防抖延迟内多次触发，只执行最后一次的异步业务逻辑。
/// </summary>
public class AsyncDebouncer<T> : IDebouncer<T>
{
    private readonly TimeSpan _delay;
    // 内部统一使用带 CancellationToken 的委托
    private readonly Func<T, CancellationToken, Task> _asyncAction;

    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;

    public CancellationToken? Token => _cts?.Token;

    /// <summary>支持取消运行中任务的异步防抖器。</summary>
    public AsyncDebouncer(TimeSpan delay, Func<T, CancellationToken, Task> asyncAction)
    {
        _delay = delay;
        _asyncAction = asyncAction;
    }

    /// <summary>不支持取消运行中任务的异步防抖器（兼容老代码）。</summary>
    public AsyncDebouncer(TimeSpan delay, Func<T, Task> asyncAction)
        : this(delay, (data, _) => asyncAction(data)) // 忽略 token
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

/// <summary>无参数的异步防抖器。</summary>
public class AsyncDebouncer : AsyncDebouncer<object?>
{
    public AsyncDebouncer(TimeSpan delay, Func<Task> asyncAction)
        : base(delay, (_) => asyncAction())
    {
    }

    /// <summary>业务逻辑支持取消运行中任务。</summary>
    public AsyncDebouncer(TimeSpan delay, Func<CancellationToken, Task> asyncAction)
        : base(delay, (_, token) => asyncAction(token))
    {
    }

    /// <summary>触发防抖（无需传参）。</summary>
    public void Trigger()
    {
        Trigger(null);
    }
}
