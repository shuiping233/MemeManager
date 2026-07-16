using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MemeManager.Data;

namespace MemeManager;

/// <summary>
/// 线程级 EcoQoS（Windows 效率模式）。
/// 仅对后台工作线程套用执行速度节流，UI / 渲染 / 输入线程完全不受影响，
/// 因此不会像“全进程降优先级”那样引发卡顿或崩溃。
/// 该 API 自 Windows 11 21H2 起可用，老系统上调用会被静默忽略。
/// </summary>
internal static partial class EcoQos
{
    // SetThreadInformation 的 ThreadInformationClass：启用/查询线程级电源节流
    private const int ThreadPowerThrottling = 4;

    // THREAD_POWER_THROTTLING_STATE.ControlMask / StateMask 的取值
    private const uint THREAD_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct THREAD_POWER_THROTTLING_STATE
    {
        public uint Version;     // 必须为 1
        public uint ControlMask; // 要控制的字段掩码
        public uint StateMask;   // 要启用的状态
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadInformation(
        IntPtr hThread, int threadInformationClass, IntPtr lpThreadInformation, uint cb);

    // ---------- 进程级 EcoQoS（任务管理器“效率模式”小绿叶）----------

    // SetProcessInformation 的 ProcessInformationClass
    private const int ProcessPowerThrottling = 4;

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(
        IntPtr hProcess, int processInformationClass, IntPtr lpProcessInformation, uint cb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const uint IDLE_PRIORITY_CLASS = 0x00000040;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;     // 必须为 1
        public uint ControlMask; // 要控制的字段掩码
        public uint StateMask;   // 要启用的状态
    }

    // 缓存当前线程是否已尝试设置，避免同一线程重复 P/Invoke
    [ThreadStatic]
    private static bool _applied;

    /// <summary>
    /// 在当前线程上套用 EcoQoS 节流（若配置开启且系统支持）。
    /// 建议在后台工作线程的入口处调用一次（线程池线程每次复用都需重新设置）。
    /// </summary>
    public static void ApplyIfEnabled()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            if (!App.DataEngine.Config.EcoMode)
            {
                Logger.Log("[EcoQos] 配置未启用，跳过线程级效率模式");
                return;
            }

            var state = new THREAD_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED
            };

            var size = (uint)Marshal.SizeOf<THREAD_POWER_THROTTLING_STATE>();
            var ptr = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.StructureToPtr(state, ptr, false);
                var ok = SetThreadInformation(
                    NativeMethods.GetCurrentThread(), ThreadPowerThrottling, ptr, size);
                Logger.Log($"[EcoQos] 已对线程 #{Environment.CurrentManagedThreadId} 启用效率模式: {(ok ? "成功" : "失败")}");
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[EcoQos] 设置线程级效率模式失败（已忽略）: {ex.Message}");
        }
    }

    // ---------- 进程级 EcoQoS ----------

    /// <summary>
    /// 对当前进程启用/关闭 Windows 效率模式（任务管理器小绿叶）。
    /// 仅设置 ProcessPowerThrottling（执行速度节流），不调整优先级类，
    /// 因此不会像旧实现那样拖慢 UI 导致卡顿。老版本 Windows 上会被忽略。
    /// </summary>
    public static void ApplyProcessLevel(bool enable)
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enable ? THREAD_POWER_THROTTLING_EXECUTION_SPEED : 0
            };

            var size = (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
            var ptr = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.StructureToPtr(state, ptr, false);
                var ok = SetProcessInformation(
                    GetCurrentProcess(), ProcessPowerThrottling, ptr, size);

                // 任务管理器的小绿叶依赖进程优先级被降级：仅设 PowerThrottling 不会显示叶子，
                // 需配合 SetPriorityClass 到 IDLE（EnergyStarX 同款做法）。
                var priOk = SetPriorityClass(
                    GetCurrentProcess(), enable ? IDLE_PRIORITY_CLASS : NORMAL_PRIORITY_CLASS);

                Logger.Log($"[EcoQos] 进程级效率模式: {(enable ? "启用" : "关闭")} " +
                           $"节流={(ok ? "成功" : "失败")} 优先级={(priOk ? "成功" : "失败")}");
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[EcoQos] 设置进程级效率模式失败（已忽略）: {ex.Message}");
        }
    }

    /// <summary>
    /// 根据当前配置对进程应用效率模式（配置开启则启用，否则关闭）。
    /// 在启动完成与设置切换时调用。
    /// </summary>
    public static void ApplyProcessLevelFromConfig()
    {
        ApplyProcessLevel(App.DataEngine.Config.EcoMode);
    }

    /// <summary>
     /// 将一段后台工作以 EcoQoS 节流方式运行在线程池上。
     /// </summary>
    public static Task RunAsync(Func<Task> work)
    {
        return Task.Run(async () =>
        {
            ApplyIfEnabled();
            await work();
        });
    }

    /// <summary>
    /// 将一段后台工作（无返回值）以 EcoQoS 节流方式运行在线程池上。
    /// </summary>
    public static Task RunAsync(Action work)
    {
        return Task.Run(() =>
        {
            ApplyIfEnabled();
            work();
        });
    }
}
