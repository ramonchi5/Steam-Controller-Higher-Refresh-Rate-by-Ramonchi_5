using System.Diagnostics;
using System.Globalization;
using SteamControllerRefreshRate.Boost;
using SteamControllerRefreshRate.Firmware;
using SteamControllerRefreshRate.Hid;
using SteamControllerRefreshRate.Native;
using SteamControllerRefreshRate.Steam;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

string cmd = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "status";

switch (cmd)
{
    case "status":
        RunStatus();
        return 0;

    case "benchmark":
    case "bench":
    {
        double seconds = ParseDoubleOption(args, "--seconds", 5.0);
        await RunBenchmarkAsync(seconds, cts.Token);
        return 0;
    }

    case "set-steam-rate":
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int targetHz) || (targetHz != 1000 && targetHz != 500))
        {
            Console.WriteLine("Usage: sc-refresh set-steam-rate <1000|500> [--restart-steam]");
            Console.WriteLine("  1000 -> Sets SteamController_Poll_Rate = 1 (1000 us = 1000 Hz)");
            Console.WriteLine("  500  -> Restores SteamController_Poll_Rate = 2 (2000 us = 500 Hz default)");
            return 1;
        }

        bool restartSteam = args.Any(a => a.Equals("--restart-steam", StringComparison.OrdinalIgnoreCase));
        return await RunSetSteamRateAsync(targetHz, restartSteam);
    }

    case "enable-persistent":
    {
        bool restartSteam = args.Any(a => a.Equals("--restart-steam", StringComparison.OrdinalIgnoreCase));
        int rc = await RunSetSteamRateAsync(1000, restartSteam);
        if (rc != 0)
        {
            return rc;
        }

        var (ok, msg) = await StartupRegistrationService.InstallAndStartStartupAgentAsync();
        Console.WriteLine(ok ? $"[OK] {msg}" : $"[ERROR] {msg}");
        return ok ? 0 : 1;
    }

    case "disable-persistent":
    {
        bool restartSteam = args.Any(a => a.Equals("--restart-steam", StringComparison.OrdinalIgnoreCase));
        var (ok, msg) = await StartupRegistrationService.UninstallAndStopStartupAgentAsync(restoreNormalSteamPriority: true);
        Console.WriteLine(ok ? $"[OK] {msg}" : $"[WARN] {msg}");

        int rc = await RunSetSteamRateAsync(500, restartSteam);
        return (ok && rc == 0) ? 0 : 1;
    }

    case "install-startup":
    {
        var (ok, msg) = await StartupRegistrationService.InstallAndStartStartupAgentAsync();
        Console.WriteLine(ok ? $"[OK] {msg}" : $"[ERROR] {msg}");
        return ok ? 0 : 1;
    }

    case "uninstall-startup":
    {
        var (ok, msg) = await StartupRegistrationService.UninstallAndStopStartupAgentAsync(restoreNormalSteamPriority: true);
        Console.WriteLine(ok ? $"[OK] {msg}" : $"[ERROR] {msg}");
        return ok ? 0 : 1;
    }

    case "stop-boost":
    {
        bool stopped = await LowLatencyBooster.StopRunningBoosterAsync(TimeSpan.FromSeconds(4), restoreNormalSteamPriority: true);
        Console.WriteLine(stopped
            ? "[OK] Stopped background Low-Latency Booster."
            : "[WARN] Background Low-Latency Booster did not exit within timeout.");
        return stopped ? 0 : 1;
    }

    case "hex2uf2":
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: sc-refresh hex2uf2 <input.hex> <output.uf2> [0xADA52840]");
            return 1;
        }

        uint familyId = Uf2Converter.Nrf52840FamilyId;
        if (args.Length >= 4)
        {
            string famText = args[3].Trim();
            if (famText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                famText = famText[2..];
            }

            if (!uint.TryParse(famText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out familyId))
            {
                Console.WriteLine($"[ERROR] Invalid hex family ID: {args[3]}");
                return 1;
            }
        }

        var summary = Uf2Converter.ConvertHexFileToUf2(args[1], args[2], familyId);
        bool belowJournal = summary.EndAddressExclusive <= Uf2Converter.OpenPuckRfJournalBase;
        Console.WriteLine($"[OK] Wrote {args[2]} ({summary.OutputBytes:N0} bytes, {summary.BlockCount} UF2 blocks)");
        Console.WriteLine($"     Flash Range : 0x{summary.StartAddress:X5} .. 0x{summary.EndAddressExclusive:X5} (Family=0x{summary.FamilyId:X8})");
        Console.WriteLine($"     Journal Cap : {(belowJournal ? "PASS" : "WARN")} (end 0x{summary.EndAddressExclusive:X5} <= 0x{Uf2Converter.OpenPuckRfJournalBase:X5})");
        return 0;
    }

    case "boost":
    case "daemon":
    {
        bool lizardOff = args.Any(a => a.Equals("--lizard-off", StringComparison.OrdinalIgnoreCase));
        await LowLatencyBooster.RunDaemonAsync(lizardOff, cts.Token);
        return 0;
    }

    case "help":
    case "--help":
    case "-h":
    default:
        PrintHelp();
        return cmd is "help" or "--help" or "-h" ? 0 : 1;
}

static void RunStatus()
{
    Console.WriteLine("=== Steam Controller Higher-Refresh-Rate Diagnostic ===");
    Console.WriteLine();

    // 1. OS Timer Resolution & Startup Booster
    Console.WriteLine("[1] Windows Kernel Timer, Background Agent & Scheduler");
    if (WindowsSchedulerNative.TryQueryTimerResolutionMs(out double maxMs, out double minMs, out double curMs))
    {
        Console.WriteLine($"    Default Tick   : {maxMs:F3} ms");
        Console.WriteLine($"    Fastest Tick   : {minMs:F3} ms");
        Console.WriteLine($"    Current Tick   : {curMs:F3} ms ({1000.0 / curMs:F0} Hz)");
    }
    else
    {
        Console.WriteLine("    Unable to query NtQueryTimerResolution.");
    }

    bool startupReg = StartupRegistrationService.IsStartupRegistered(out string? regCmd);
    bool agentRunning = LowLatencyBooster.IsBoosterRunning(out int? agentPid);
    string regLabel = startupReg ? "Installed (HKCU\\...\\Run)" : "Not installed";
    string runLabel = agentRunning
        ? (agentPid.HasValue ? $"Running (sc-boost-agent PID {agentPid.Value})" : "Running")
        : "Stopped";
    Console.WriteLine($"    Startup Agent  : {regLabel} | {runLabel}");
    if (startupReg && !string.IsNullOrWhiteSpace(regCmd))
    {
        Console.WriteLine($"    Startup Target : {regCmd}");
    }

    if (SteamInputConfigService.IsSteamRunning(out var steamProcs))
    {
        foreach (var p in steamProcs)
        {
            using (p)
            {
                try
                {
                    Console.WriteLine($"    steam.exe      : Running (PID {p.Id}, Priority={p.PriorityClass})");
                }
                catch
                {
                    Console.WriteLine($"    steam.exe      : Running (PID {p.Id})");
                }
            }
        }
    }
    else
    {
        Console.WriteLine("    steam.exe      : Not running (or isolated from current session)");
    }

    Console.WriteLine();

    // 2. Steam Input Persistent Configuration & Official Firmware Targets
    Console.WriteLine("[2] Steam Input Translation Rate & Official Firmware Targets");
    string? steamPath = SteamInputConfigService.FindSteamInstallPath();
    SteamFirmwareTargets? fwTargets = null;
    if (steamPath is null)
    {
        Console.WriteLine("    Steam installation not found.");
    }
    else
    {
        Console.WriteLine($"    Steam Path   : {steamPath}");
        var configs = SteamInputConfigService.InspectLocalConfigs(steamPath);
        if (configs.Count == 0)
        {
            Console.WriteLine("    No userdata localconfig.vdf files found.");
        }
        else
        {
            foreach (var cfg in configs)
            {
                string rawLabel = cfg.ConfiguredPollRateMs.HasValue
                    ? $"{cfg.ConfiguredPollRateMs.Value} ms (explicit)"
                    : "unset -> defaults to 2 ms";
                Console.WriteLine($"    Account {cfg.AccountId}: SteamController_Poll_Rate = {rawLabel} => {cfg.EffectiveSteamInputHz} Hz ({(cfg.ConfiguredPollRateMs ?? 2) * 1000} us)");
            }
        }

        fwTargets = SteamInputConfigService.TryReadHardwareUpdaterTargets(steamPath);
        if (fwTargets is not null)
        {
            string ctrlTarget = fwTargets.TritonControllerFwTimestamp.HasValue
                ? $"0x{fwTargets.TritonControllerFwTimestamp.Value:X8}"
                : "n/a";
            string puckTarget = fwTargets.ProteusPuckFwTimestamp.HasValue
                ? $"0x{fwTargets.ProteusPuckFwTimestamp.Value:X8}"
                : "n/a";
            Console.WriteLine($"    Valve FW Cfg : Controller (TRITON_FW_TS) = {ctrlTarget} | Puck (PROTEUS_FW_TS) = {puckTarget}");
        }
    }

    Console.WriteLine();

    // 3. Connected Valve Hardware
    Console.WriteLine("[3] Connected Valve USB / Wireless HID Interfaces (VID 0x28DE)");
    var devices = SteamControllerScanner.ScanDevices();
    if (devices.Count == 0)
    {
        Console.WriteLine("    No active Valve HID interfaces detected right now.");
        Console.WriteLine("    (Ensure the Steam Controller Wireless Puck 28DE:1304 is plugged in and controller is awake.)");
    }
    else
    {
        foreach (var d in devices)
        {
            string fw = "n/a";
            if (d.FirmwareTimestamp.HasValue)
            {
                bool isPuckFw = d.Usage == 0x0002 ||
                                d.ReportedAttrProductId is SteamControllerScanner.ProteusPuckPid or SteamControllerScanner.SteamMachineReceiverPid;
                uint? expectedTs = isPuckFw
                    ? fwTargets?.ProteusPuckFwTimestamp
                    : fwTargets?.TritonControllerFwTimestamp;
                string badge = expectedTs.HasValue
                    ? (d.FirmwareTimestamp.Value < expectedTs.Value ? " [UPDATE AVAILABLE]" : " [UP TO DATE]")
                    : "";
                string emptyHint = (d.Usage == 0x0001 && isPuckFw) ? " (empty slot)" : "";
                fw = $"0x{d.FirmwareTimestamp.Value:X8}{badge}{emptyHint}";
            }

            string bat = d.BatteryPercent.HasValue ? $", Battery={d.BatteryPercent.Value}%" : "";
            Console.WriteLine($"    - PID 0x{d.ProductId:X4} | Usage 0x{d.UsagePage:X4}:0x{d.Usage:X4} | In={d.InputReportLength}B Feat={d.FeatureReportLength}B | FW={fw}{bat}");
            Console.WriteLine($"      Role : {d.RoleDescription} (Product='{d.ProductName ?? "n/a"}', Serial='{d.SerialNumber ?? "n/a"}')");
        }
    }
}

static async Task RunBenchmarkAsync(double seconds, CancellationToken cancellationToken)
{
    Console.WriteLine($"=== Raw HID Polling & Unique Frame Rate Benchmark ({seconds:F1}s) ===");
    var devices = SteamControllerScanner.ScanDevices();
    if (devices.Count == 0)
    {
        Console.WriteLine("No Valve HID interfaces (VID 0x28DE) detected.");
        Console.WriteLine("Plug in the Steam Controller Puck (or cable), wake the controller, and move sticks/pads during the test.");
        return;
    }

    Console.WriteLine($"Found {devices.Count} Valve HID interface(s). Move sticks/touchpads/gyro continuously for {seconds:F1} seconds...");
    var results = await PollingBenchmarker.RunAsync(devices, seconds, cancellationToken);

    var active = results.Where(r => r.TotalReports > 0).ToArray();
    if (active.Length == 0)
    {
        Console.WriteLine();
        Console.WriteLine("0 input reports received across all candidate interfaces.");
        Console.WriteLine("The Wireless Puck may be plugged in while the Steam Controller itself is powered off / asleep.");
        return;
    }

    foreach (var r in active)
    {
        Console.WriteLine();
        Console.WriteLine($"Interface : {r.Device.RoleDescription} (PID 0x{r.Device.ProductId:X4}, Usage 0x{r.Device.UsagePage:X4}:0x{r.Device.Usage:X4})");
        Console.WriteLine($"Duration  : {r.DurationSeconds:F3} s | State RID: 0x{r.LastReportId:X2} | Sample[0..16]: {r.FirstSampleHex}");
        Console.WriteLine($"Reports   : {r.TotalReports} total ({r.StateReports} Triton state 0x42/0x45/0x47, {r.AuxReports} aux status 0x43/0x79/0x7B)");
        Console.WriteLine($"  -> USB HID Delivery Rate      : {r.UsbReportHz,7:F2} Hz");
        if (r.StateReports > 0)
        {
            Console.WriteLine($"  -> State Report Delivery Rate : {r.StateReportHz,7:F2} Hz  (every {r.AvgDeltaMs:F3} ms)");
            Console.WriteLine($"  -> True Unique Sequence Rate  : {r.UniqueSeqHz,7:F2} Hz  ({r.UniqueSeqFrames} unique seqs, {r.DuplicateSeqFrames} dupes, {r.MissedSeqGaps} sequence jumps)");
        }

        Console.WriteLine($"  -> Unique Sensor Payload Rate : {r.UniquePayloadHz,7:F2} Hz  ({r.UniquePayloadFrames} distinct input/ADC payloads)");
        Console.WriteLine($"  -> State Frame Timing (ms)    : min={r.MinDeltaMs:F3} | p50={r.P50DeltaMs:F3} | avg={r.AvgDeltaMs:F3} | p95={r.P95DeltaMs:F3} | p99={r.P99DeltaMs:F3} | max={r.MaxDeltaMs:F3} | jitter(sd)={r.JitterStdDevMs:F3}");
    }
}

static async Task<int> RunSetSteamRateAsync(int targetHz, bool restartSteam)
{
    string? steamPath = SteamInputConfigService.FindSteamInstallPath();
    if (steamPath is null)
    {
        Console.WriteLine("[ERROR] Could not locate Steam installation directory.");
        return 1;
    }

    int pollRateMs = targetHz == 1000 ? 1 : 2;
    int controllerRateUs = pollRateMs * 1000;

    bool wasRunning = SteamInputConfigService.IsSteamRunning(out var procs);
    foreach (var p in procs) p.Dispose();

    if (wasRunning && !restartSteam)
    {
        Console.WriteLine("[IMPORTANT] Steam is currently running!");
        Console.WriteLine("Steam overwrites localconfig.vdf from memory when exiting.");
        Console.WriteLine("Either close Steam first and re-run this command, or pass --restart-steam to let this tool cleanly close and relaunch Steam:");
        Console.WriteLine($"  sc-refresh set-steam-rate {targetHz} --restart-steam");
        return 2;
    }

    if (wasRunning && restartSteam)
    {
        Console.WriteLine("Shutting down Steam gracefully (steam.exe -shutdown)...");
        bool closed = await SteamInputConfigService.ShutdownSteamGracefullyAsync(steamPath, TimeSpan.FromSeconds(25));
        if (!closed)
        {
            Console.WriteLine("[ERROR] Steam did not exit within 25 seconds. Close Steam manually and retry.");
            return 1;
        }
    }

    var updatedFiles = SteamInputConfigService.ApplyPollRateMsToAllUsers(steamPath, pollRateMs);
    foreach (string file in updatedFiles)
    {
        Console.WriteLine($"[OK] Updated {file} -> \"SteamController_Poll_Rate\" \"{pollRateMs}\" ({targetHz} Hz / {controllerRateUs} us) [backup saved]");
    }

    if (wasRunning && restartSteam)
    {
        Console.WriteLine($"Relaunching Steam with +controller_rate {controllerRateUs}...");
        SteamInputConfigService.LaunchSteam(steamPath, controllerRateUs);
    }

    return 0;
}

static double ParseDoubleOption(string[] args, string flag, double fallback)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out double v) &&
            v > 0.2 && v <= 120)
        {
            return v;
        }
    }

    return fallback;
}

static void PrintHelp()
{
    Console.WriteLine("Steam Controller Higher-Refresh-Rate Tool (sc-refresh)");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  status                                      Inspect Valve HID hardware, firmware, Steam Input VDF rate, and OS timer tick");
    Console.WriteLine("  benchmark [--seconds 5]                     Measure live raw HID USB Hz, true unique RF sequence Hz, and jitter percentiles");
    Console.WriteLine("  enable-persistent [--restart-steam]         Apply 1000 Hz Steam Input + install & start silent 0.5ms Windows startup booster");
    Console.WriteLine("  disable-persistent [--restart-steam]        Revert Steam Input to 500 Hz + remove & stop Windows startup booster");
    Console.WriteLine("  set-steam-rate <1000|500> [--restart-steam] Configure persistent Steam Input 1000 Hz (or revert to 500 Hz) in localconfig.vdf");
    Console.WriteLine("  install-startup                             Register & start silent windowless sc-boost-agent.exe on Windows login");
    Console.WriteLine("  uninstall-startup                           Remove sc-boost-agent.exe from Windows login & stop running background agent");
    Console.WriteLine("  boost [--lizard-off]                        Run interactive foreground 0.5ms Windows kernel timer & steam.exe booster");
    Console.WriteLine("  hex2uf2 <input.hex> <output.uf2>            Convert compiled nRF52840 Intel HEX firmware into drag-and-drop UF2");
}
