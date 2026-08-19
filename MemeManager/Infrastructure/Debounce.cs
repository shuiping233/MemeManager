namespace MemeManager.Infrastructure;

// TODO AsyncDebounce类缺单元测试
public interface IDebouncer<T>
{
    public CancellationToken? Token { get; }
    public void Trigger(T data);
    public void CancelPending();
}

/// <summary>
/// 同步防抖器。
/// 继承自 <see cref="AsyncDebouncer{T}"/>，将同步业务逻辑包装为异步委托执行。
/// 适用于需要防抖且业务逻辑为纯同步操作（如更新本地内存变量、简单日志记录）的场景。
/// </summary>
/// <typeparam name="T">防抖处理的数据类型</typeparam>
/// <example>
/// 以下示例演示如何使用 <see cref="Debouncer{T}"/> 防抖更新本地变量：
/// <code>
/// // 创建防抖器：间隔 300ms 执行一次同步操作
/// var debouncer = new Debouncer&lt;string&gt;(
///     TimeSpan.FromMilliseconds(300),
///     (data) =>
///     {
///         // 这是一个同步操作
///         Console.WriteLine($"处理最新数据: {data}");
///     });
/// ///
/// // 模拟连续触发
/// debouncer.Trigger("A");
/// debouncer.Trigger("B");
/// debouncer.Trigger("C"); // 最终只会输出 "处理最新数据: C"
/// </code>
/// </example>
public class Debouncer<T>(TimeSpan delay, Action<T> action) : AsyncDebouncer<T>(delay, t =>
    {
        action(t);
        return Task.CompletedTask;
    })
{ }

/// <summary>
/// 无参数的同步防抖器。复用泛型防抖器逻辑，提供更干净的无参 API。
/// </summary>
/// <remarks>
/// 初始化无参数防抖器
/// </remarks>
/// <param name="delay">防抖延迟时间</param>
/// <param name="Action">无参数的同步委托</param>
public class Debouncer : Debouncer<object?>
{
    /// <summary>
    /// 初始化无参数防抖器（业务逻辑不支持取消）
    /// </summary>
    /// <param name="delay">防抖延迟时间</param>
    /// <param name="action">无参数的异步委托</param>
    public Debouncer(TimeSpan delay, Action action)
        : base(delay, _ =>
        {
            action();
        })
    { }

    /// <summary>
    /// 触发防抖（无需传参）
    /// </summary>
    public void Trigger()
    {
        Trigger(null);
    }
}

/// <summary>
/// 异步防抖器。
/// 在指定的防抖延迟时间内，如果多次触发，只会执行最后一次触发的异步业务逻辑。
/// 适用于需要防抖且业务逻辑包含异步操作（如保存到数据库、调用API）的场景。
/// </summary>
/// <typeparam name="T">防抖处理的数据类型</typeparam>
/// <remarks>
/// 使用时需提供防抖等待时间和异步处理委托。
/// </remarks>
public class AsyncDebouncer<T> : IDebouncer<T>
{
    private readonly TimeSpan _delay;
    // 内部统一使用带 CancellationToken 的委托
    private readonly Func<T, CancellationToken, Task> _asyncAction;

    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;

    public CancellationToken? Token => _cts?.Token;

    /// <summary>
    /// 构造函数 1：支持取消运行中任务的异步防抖器
    /// </summary>
    public AsyncDebouncer(TimeSpan delay, Func<T, CancellationToken, Task> asyncAction)
    {
        _delay = delay;
        _asyncAction = asyncAction;
    }

    /// <summary>
    /// 构造函数 2：不支持取消运行中任务的异步防抖器 (兼容老代码)
    /// </summary>
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
    /// 默认只能取消未执行的Task, 如果被触发的Task支持且传入了CancellationTokenSource, 则正在触发的Task会被一起取消
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

/// <summary>
/// 无参数的异步防抖器。复用泛型防抖器逻辑，提供更干净的无参 API。
/// </summary>
/// <remarks>
/// 初始化无参数防抖器
/// </remarks>
/// <param name="delay">防抖延迟时间</param>
/// <param name="asyncAction">无参数的异步委托</param>
public class AsyncDebouncer : AsyncDebouncer<object?>
{
    /// <summary>
    /// 初始化无参数防抖器（业务逻辑不支持取消）
    /// </summary>
    /// <param name="delay">防抖延迟时间</param>
    /// <param name="asyncAction">无参数的异步委托</param>
    public AsyncDebouncer(TimeSpan delay, Func<Task> asyncAction)
        : base(delay, (_) => asyncAction())
    {
    }

    /// <summary>
    /// 初始化无参数防抖器（业务逻辑支持取消运行中任务）
    /// </summary>
    /// <param name="delay">防抖延迟时间</param>
    /// <param name="asyncAction">带 CancellationToken 的异步委托</param>
    public AsyncDebouncer(TimeSpan delay, Func<CancellationToken, Task> asyncAction)
        : base(delay, (_, token) => asyncAction(token))
    {
    }

    /// <summary>
    /// 触发防抖（无需传参）
    /// </summary>
    public void Trigger()
    {
        Trigger(null);
    }
}
