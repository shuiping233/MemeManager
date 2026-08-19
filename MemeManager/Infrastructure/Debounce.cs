namespace MemeManager.Infrastructure;

public interface IDebouncer<T>
{
    public void Trigger(T data);
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
/// 异步防抖器。
/// 在指定的防抖延迟时间内，如果多次触发，只会执行最后一次触发的异步业务逻辑。
/// 适用于需要防抖且业务逻辑包含异步操作（如保存到数据库、调用API）的场景。
/// </summary>
/// <typeparam name="T">防抖处理的数据类型</typeparam>
/// <remarks>
/// 使用时需提供防抖等待时间和异步处理委托。
/// </remarks>
public class AsyncDebouncer<T>(TimeSpan delay, Func<T, Task> asyncAction) : IDebouncer<T>
{

    // 同步锁，保证并发安全（因为替换 CTS 是极快的内存操作，用同步锁即可，无需异步锁）
    private readonly Lock _lock = new();

    private CancellationTokenSource? _cts;

    /// <summary>
    /// 触发防抖。传入业务数据，如果在延迟时间内再次触发，则重置计时器。
    /// </summary>
    /// <param name="data">要处理的业务数据</param>
    /// <example>
    /// 以下示例演示如何使用 <see cref="AsyncDebouncer{T}"/> 防抖保存数据：
    /// <code>
    /// // 创建防抖器：间隔 500ms 执行一次异步保存
    /// var debouncer = new AsyncDebouncer&lt;string&gt;(
    ///     TimeSpan.FromMilliseconds(500),
    ///     async (data) =>
    ///     {
    ///         Console.WriteLine($"开始异步保存: {data}");
    ///         await Task.Delay(1000); // 模拟耗时的数据库操作
    ///         Console.WriteLine($"保存完成: {data}");
    ///     });
    ///
    /// // 模拟连续触发
    /// debouncer.Trigger("数据1");
    /// debouncer.Trigger("数据2");
    /// debouncer.Trigger("数据3"); // 最终只有 "数据3" 会被保存
    /// </code>
    /// </example>
    public void Trigger(T data)
    {
        lock (_lock)
        {
            // 1. 取消之前的延迟任务（相当于重置计时器）
            _cts?.Cancel();
            _cts?.Dispose();

            // 2. 创建新的 CTS
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // 3. 启动后台异步任务（不需要 await，让它后台运行）
            _ = ExecuteAsync(data, token);
        }
    }

    /// <summary>
    /// 真正执行防抖等待和业务逻辑的异步方法
    /// </summary>
    private async Task ExecuteAsync(T data, CancellationToken token)
    {
        try
        {
            // 1. 异步等待防抖时间（这期间不会阻塞任何线程）
            // 如果在等待期间又触发了 Trigger，token 会被取消，直接跳到 catch
            await Task.Delay(delay, token);

            // 2. 如果 Delay 顺利结束，说明防抖时间到了，且这是最后一次触发
            // 执行异步业务逻辑
            await asyncAction(data);
        }
        catch (TaskCanceledException)
        {
            // 被取消是正常行为，直接忽略
            // 这意味着这次触发被新的触发覆盖了，不需要执行业务
        }
    }
}
