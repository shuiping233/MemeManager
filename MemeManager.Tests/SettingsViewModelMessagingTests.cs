using CommunityToolkit.Mvvm.Messaging;
using MemeManager.Models;
using MemeManager.Services;
using MemeManager.ViewModels;
using Xunit;

namespace MemeManager.Tests;

// 设置页 VM 把"需要页面配合的用户意图"以 WeakReferenceMessenger 单向消息广播出去，
// 页面订阅后执行依赖窗口/文件选择器/XamlRoot 的副作用。
// 这里锁住「命令 → 消息」的契约：以后若有人改回 VM 上的委托属性、或漏发消息，测试会立刻失败。
public class SettingsViewModelMessagingTests
{
    // 通过 xUnit 的"每个 [Fact] 新建测试类实例"获得天然的唯一 recipient：
    // WeakReferenceMessenger 对同一实例重复注册同一消息会抛 InvalidOperationException。
    private static SettingsViewModel CreateViewModel() => new(new UpdateService([]));

    [Fact]
    public void BrowseFolderCommand_SendsBrowseFolderRequested()
    {
        var vm = CreateViewModel();
        bool received = false;
        WeakReferenceMessenger.Default.Register<BrowseFolderRequestedMessage>(this, (_, _) => received = true);

        vm.BrowseFolderCommand.Execute(null);

        Assert.True(received);
    }

    [Fact]
    public void OpenMemeDataFolderCommand_CarriesPathInMessage()
    {
        var vm = CreateViewModel();
        string? receivedPath = null;
        WeakReferenceMessenger.Default.Register<OpenFolderRequestedMessage>(this, (_, m) => receivedPath = m.Path);

        vm.OpenMemeDataFolderCommand.Execute(@"C:\memes");

        Assert.Equal(@"C:\memes", receivedPath);
    }

    [Fact]
    public void CloseCommand_SendsCloseSettingsRequested()
    {
        var vm = CreateViewModel();
        bool received = false;
        WeakReferenceMessenger.Default.Register<CloseSettingsRequestedMessage>(this, (_, _) => received = true);

        vm.CloseCommand.Execute(null);

        Assert.True(received);
    }

    [Fact]
    public void AboutCommand_SendsAboutRequested()
    {
        var vm = CreateViewModel();
        bool received = false;
        WeakReferenceMessenger.Default.Register<AboutRequestedMessage>(this, (_, _) => received = true);

        vm.AboutCommand.Execute(null);

        Assert.True(received);
    }

    [Fact]
    public void ProgramExitCommand_SendsProgramExitRequested()
    {
        var vm = CreateViewModel();
        bool received = false;
        WeakReferenceMessenger.Default.Register<ProgramExitRequestedMessage>(this, (_, _) => received = true);

        vm.ProgramExitCommand.Execute(null);

        Assert.True(received);
    }

    // 反向哨兵：命令只广播、不再往 VM 上写委托属性（防止有人把 Action 属性加回来）。
    [Fact]
    public void ViewModel_ExposesNoPageCallbackProperties()
    {
        var vm = CreateViewModel();

        var callbackProps = typeof(SettingsViewModel)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(Action)
                     || p.PropertyType == typeof(Action<string>))
            .ToArray();

        Assert.Empty(callbackProps);
    }
}
