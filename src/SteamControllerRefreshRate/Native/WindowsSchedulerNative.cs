using System.Runtime.InteropServices;

namespace SteamControllerRefreshRate.Native;

internal static class WindowsSchedulerNative
{
    public const int ProcessPowerThrottling = 4;
    public const uint ProcessPowerThrottlingCurrentVersion = 1;
    public const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    public const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;

    [DllImport("ntdll.dll")]
    public static extern int NtQueryTimerResolution(
        out uint maximumTime100Ns,
        out uint minimumTime100Ns,
        out uint currentTime100Ns);

    [DllImport("ntdll.dll")]
    public static extern int NtSetTimerResolution(
        uint desiredTime100Ns,
        [MarshalAs(UnmanagedType.U1)] bool setResolution,
        out uint currentTime100Ns);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        int processInformationSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public static bool TryQueryTimerResolutionMs(out double maxMs, out double minMs, out double currentMs)
    {
        maxMs = minMs = currentMs = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        int status = NtQueryTimerResolution(out uint max100Ns, out uint min100Ns, out uint cur100Ns);
        if (status != 0)
        {
            return false;
        }

        maxMs = max100Ns / 10_000.0;
        minMs = min100Ns / 10_000.0;
        currentMs = cur100Ns / 10_000.0;
        return true;
    }

    public static bool TryLockMaxTimerResolution(out double appliedMs, out uint locked100Ns, out bool usedWinMmFallback)
    {
        appliedMs = 0;
        locked100Ns = 0;
        usedWinMmFallback = false;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        uint target100Ns = 5_000; // 0.500 ms
        if (NtQueryTimerResolution(out _, out uint min100Ns, out _) == 0 && min100Ns > 0)
        {
            target100Ns = Math.Max(min100Ns, 5_000u);
        }

        int status = NtSetTimerResolution(target100Ns, true, out uint cur100Ns);
        if (status != 0)
        {
            timeBeginPeriod(1);
            usedWinMmFallback = true;
            if (NtQueryTimerResolution(out _, out _, out cur100Ns) != 0)
            {
                return false;
            }
        }
        else
        {
            locked100Ns = target100Ns;
        }

        appliedMs = cur100Ns / 10_000.0;
        return true;
    }

    public static bool TryLockMaxTimerResolution(out double appliedMs) =>
        TryLockMaxTimerResolution(out appliedMs, out _, out _);

    public static void UnlockTimerResolution(uint locked100Ns, bool usedWinMmFallback)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (usedWinMmFallback)
        {
            timeEndPeriod(1);
        }
        else if (locked100Ns > 0)
        {
            NtSetTimerResolution(locked100Ns, false, out _);
        }
    }

    public static bool TryDisablePowerThrottling(IntPtr processHandle)
    {
        if (!OperatingSystem.IsWindows() || processHandle == IntPtr.Zero)
        {
            return false;
        }

        // Disable both ExecutionSpeed throttling (EcoQoS) and IgnoreTimerResolution throttling
        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = ProcessPowerThrottlingExecutionSpeed | ProcessPowerThrottlingIgnoreTimerResolution,
            StateMask = 0,
        };

        return SetProcessInformation(
            processHandle,
            ProcessPowerThrottling,
            ref state,
            Marshal.SizeOf<ProcessPowerThrottlingState>());
    }
}
