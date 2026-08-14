using MemeManager.Services;
using Xunit;

namespace MemeManager.Tests;

public class UpdateServiceTests
{
    // 可编程的假客户端：控制返回结果与耗时
    private sealed class FakeClient : IUpdateServiceClient
    {
        private readonly Func<CancellationToken, Task<string?>> _impl;
        public string SourceName { get; }

        public FakeClient(string name, Func<CancellationToken, Task<string?>> impl)
        {
            SourceName = name;
            _impl = impl;
        }

        public Task<string?> GetLatestVersionAsync(CancellationToken ct = default)
            => _impl(ct);
    }

    [Fact]
    public async Task CheckAsync_FirstSuccessWins_AndOthersCancelled()
    {
        var slowCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new FakeClient("slow", async ct =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                slowCancelled.TrySetResult(true);
            }
            return null;
        });
        var fast = new FakeClient("fast", _ => Task.FromResult<string?>("v1.2.3"));

        var svc = new UpdateService(new[] { slow, fast });
        await svc.CheckAsync();

        Assert.Equal("v1.2.3", svc.LatestVersion);
        // 慢源确实被取消（而不是等它自然完成）
        await slowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(UpdateCheckState.Checking, svc.CheckState);
        Assert.False(svc.IsChecking);
    }

    [Fact]
    public async Task CheckAsync_AllClientsFail_StateIsFailed()
    {
        var svc = new UpdateService(new[]
        {
            new FakeClient("a", _ => Task.FromResult<string?>(null)),
            new FakeClient("b", _ => Task.FromResult<string?>(null)),
        });

        await svc.CheckAsync();

        Assert.Null(svc.LatestVersion);
        Assert.False(svc.HasNewVersion);
        Assert.Equal(UpdateCheckState.Failed, svc.CheckState);
    }

    [Fact]
    public async Task CheckAsync_ReentrantCall_IsIgnored()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient("x", async _ =>
        {
            started.TrySetResult(true);
            await release.Task;
            return "v1.2.3";
        });

        var svc = new UpdateService(new[] { client });
        var first = svc.CheckAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 进行中再次调用：应被短路立即返回，不并发执行
        await svc.CheckAsync();
        Assert.True(svc.IsChecking);

        release.TrySetResult(true);
        await first;
        Assert.False(svc.IsChecking);
    }

    [Fact]
    public async Task CheckAsync_FailedClientThrows_StillContinues()
    {
        // 单个源抛异常不应影响整体：另一个源正常返回
        var throwing = new FakeClient("bad", _ => throw new InvalidOperationException("boom"));
        var good = new FakeClient("good", _ => Task.FromResult<string?>("v2.0.0"));

        var svc = new UpdateService(new[] { throwing, good });
        await svc.CheckAsync();

        Assert.Equal("v2.0.0", svc.LatestVersion);
    }
}
