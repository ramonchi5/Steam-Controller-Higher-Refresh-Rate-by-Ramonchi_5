using System.ComponentModel;
using System.Diagnostics;
using SteamControllerRefreshRate.Hid;
using SteamControllerRefreshRate.Native;
using SteamControllerRefreshRate.Steam;

namespace SteamControllerRefreshRate.Boost;

public static class LowLatencyBooster
{
    public const string SingletonMutexName = @"Local\SteamControllerLowLatencyBoost_Mutex";
    public const string StopEventName = @"Local\SteamControllerLowLatencyBoost_StopEvent";

    public static bool IsBoosterRunning(out int? agentPid)
    {
        agentPid = null;
        try
        {
            var procs = Process.GetProcessesByName("sc-boost-agent");
            if (procs.Length > 0)
            {
                agentPid = procs[0].Id;
                foreach (var p in procs) p.Dispose();
                return true;
            }
        }
        catch
        {
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (Mutex.TryOpenExisting(SingletonMutexName, out var existing))
                {
                    existing.Dispose();
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    public static async Task<bool> StopRunningBoosterAsync(TimeSpan timeout, bool restoreNormalSteamPriority = false)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(StopEventName, out var evt))
                {
                    using (evt)
                    {
                        evt.Set();
                    }
                }
            }
            catch
            {
            }
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!IsBoosterRunning(out _))
            {
                break;
            }

            await Task.Delay(100);
        }

        // Fallback cleanup if a stray process did not exit on event signal
        try
        {
            foreach (var p in Process.GetProcessesByName("sc-boost-agent"))
            {
                using (p)
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.Kill();
                            p.WaitForExit(2000);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        if (restoreNormalSteamPriority)
        {
            BoostRunningSteamProcesses(ProcessPriorityClass.Normal);
        }

        return !IsBoosterRunning(out _);
    }

    public static (int BoostedCount, string Summary) BoostRunningSteamProcesses(
        ProcessPriorityClass targetPriority = ProcessPriorityClass.High,
        string? expectedSteamDir = null,
        HashSet<int>? unthrottledPidCache = null)
    {
        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName("steam");
        }
        catch (Exception ex)
        {
            return (0, $"Unable to query steam.exe processes: {ex.Message}");
        }

        if (procs.Length == 0)
        {
            unthrottledPidCache?.Clear();
            return (0, "steam.exe is not currently running.");
        }

        expectedSteamDir ??= SteamInputConfigService.FindSteamInstallPath();
        string? expectedSteamExe = expectedSteamDir is not null
            ? Path.GetFullPath(Path.Combine(expectedSteamDir, "steam.exe"))
            : null;

        var livePids = new HashSet<int>();
        int boosted = 0;
        var details = new List<string>();
        foreach (var proc in procs)
        {
            using (proc)
            {
                try
                {
                    if (expectedSteamExe is not null && !MatchesExpectedSteamExecutable(proc, expectedSteamExe))
                    {
                        continue;
                    }

                    livePids.Add(proc.Id);
                    bool alreadyUnthrottled = unthrottledPidCache is not null && unthrottledPidCache.Contains(proc.Id);
                    if (proc.PriorityClass != targetPriority)
                    {
                        proc.PriorityClass = targetPriority;
                    }

                    bool unthrottled = targetPriority != ProcessPriorityClass.Normal &&
                                       (alreadyUnthrottled || WindowsSchedulerNative.TryDisablePowerThrottling(proc.Handle));
                    if (unthrottled)
                    {
                        unthrottledPidCache?.Add(proc.Id);
                    }
                    else
                    {
                        unthrottledPidCache?.Remove(proc.Id);
                    }

                    boosted++;
                    details.Add($"PID {proc.Id} -> Priority={proc.PriorityClass}, Win11TimerUnthrottled={unthrottled}");
                }
                catch (Exception ex)
                {
                    details.Add($"PID {proc.Id} -> {ex.Message}");
                }
            }
        }

        unthrottledPidCache?.IntersectWith(livePids);

        if (details.Count == 0)
        {
            return (0, "Valve steam.exe is not currently running.");
        }

        return (boosted, string.Join("; ", details));
    }

    private static bool MatchesExpectedSteamExecutable(Process proc, string expectedSteamExe)
    {
        try
        {
            string? actualPath = proc.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath))
            {
                return true;
            }

            return string.Equals(Path.GetFullPath(actualPath), expectedSteamExe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception)
        {
            // Access denied when querying MainModule of an elevated/protected process; allow by process name.
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task RunDaemonAsync(bool suppressLizardMode, CancellationToken cancellationToken, bool quiet = false)
    {
        Mutex? singletonMutex = null;
        EventWaitHandle? stopEvent = null;
        RegisteredWaitHandle? stopWaitRegistration = null;
        uint lockedTimer100Ns = 0;
        bool usedWinMmFallback = false;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (OperatingSystem.IsWindows())
        {
            singletonMutex = new Mutex(initiallyOwned: true, SingletonMutexName, out bool createdNew);
            if (!createdNew)
            {
                if (!quiet)
                {
                    Console.WriteLine("[INFO] Another Low-Latency Booster instance is already running.");
                }

                singletonMutex.Dispose();
                return;
            }

            stopEvent = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, StopEventName);
            stopEvent.Reset();
            stopWaitRegistration = ThreadPool.RegisterWaitForSingleObject(
                stopEvent,
                static (state, _) => ((CancellationTokenSource)state!).Cancel(),
                linkedCts,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }

        try
        {
            if (!quiet)
            {
                Console.WriteLine("=== Steam Controller Low-Latency Kernel Timer & Priority Booster ===");
            }

            if (WindowsSchedulerNative.TryLockMaxTimerResolution(out double appliedMs, out lockedTimer100Ns, out usedWinMmFallback))
            {
                if (!quiet)
                {
                    Console.WriteLine($"[OK] Locked Windows kernel timer resolution to {appliedMs:F3} ms ({1000.0 / appliedMs:F0} Hz tick).");
                }
            }
            else if (!quiet)
            {
                Console.WriteLine("[WARN] Could not lock NtSetTimerResolution.");
            }

            // Disable power/timer throttling on our own booster process as well
            using (var self = Process.GetCurrentProcess())
            {
                WindowsSchedulerNative.TryDisablePowerThrottling(self.Handle);
            }

            string? steamInstallDir = SteamInputConfigService.FindSteamInstallPath();
            var unthrottledSteamPids = new HashSet<int>();
            string lastSteamState = string.Empty;
            var lizardSuppressedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!quiet)
            {
                Console.WriteLine("Monitoring steam.exe & Valve HID interfaces... Press Ctrl+C to stop.");
            }

            try
            {
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    var (steamCount, summary) = BoostRunningSteamProcesses(
                        ProcessPriorityClass.High,
                        steamInstallDir,
                        unthrottledSteamPids);

                    if (!quiet && !string.Equals(summary, lastSteamState, StringComparison.Ordinal))
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Steam Input Scheduler: {summary}");
                        lastSteamState = summary;
                    }

                    // Only send standalone Lizard Mode suppression when Steam is NOT running (Steam manages id9 itself).
                    if (suppressLizardMode && steamCount == 0)
                    {
                        var devices = SteamControllerScanner.ScanDevices();
                        foreach (var dev in devices.Where(d => d.CanWrite && d.InputReportLength >= 48 && d.UsagePage >= 0xFF00 && d.Usage == 0x0001))
                        {
                            if (lizardSuppressedPaths.Add(dev.DevicePath))
                            {
                                using var h = SteamControllerScanner.OpenDeviceHandle(dev.DevicePath, out bool writable);
                                if (!h.IsInvalid && writable && SteamControllerScanner.TryDisableLizardMode(h, dev.FeatureReportLength))
                                {
                                    if (!quiet)
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Disabled Lizard Mode overhead on {dev.RoleDescription}");
                                    }
                                }
                            }
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), linkedCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }

            if (!quiet)
            {
                Console.WriteLine("Stopped Low-Latency Booster (Windows timer resolution restored).");
            }
        }
        finally
        {
            WindowsSchedulerNative.UnlockTimerResolution(lockedTimer100Ns, usedWinMmFallback);
            stopWaitRegistration?.Unregister(null);
            stopEvent?.Dispose();
            if (singletonMutex is not null)
            {
                try
                {
                    singletonMutex.ReleaseMutex();
                }
                catch
                {
                }

                singletonMutex.Dispose();
            }
        }
    }
}
