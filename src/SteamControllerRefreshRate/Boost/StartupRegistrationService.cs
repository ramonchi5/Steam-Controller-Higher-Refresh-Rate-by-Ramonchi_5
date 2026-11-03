using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace SteamControllerRefreshRate.Boost;

public static class StartupRegistrationService
{
    public const string RunRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "SteamControllerLowLatencyBoost";

    public static bool IsStartupRegistered(out string? registeredCommand)
    {
        registeredCommand = null;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistrySubKey, writable: false);
            if (key?.GetValue(RunValueName) is string val && !string.IsNullOrWhiteSpace(val))
            {
                registeredCommand = val;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public static string? FindBoostAgentExecutable()
    {
        string baseDir = AppContext.BaseDirectory;

        // 1. Same directory as sc-refresh.exe
        string sameDirCandidate = Path.Combine(baseDir, "sc-boost-agent.exe");
        if (File.Exists(sameDirCandidate))
        {
            return Path.GetFullPath(sameDirCandidate);
        }

        // 2. Sibling project build output, preferring the configuration matching sc-refresh.exe
        bool isDebug = baseDir.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        string[] configs = isDebug ? ["Debug", "Release"] : ["Release", "Debug"];

        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            foreach (string cfg in configs)
            {
                string candidate = Path.Combine(dir.FullName, "src", "SteamControllerBoostAgent", "bin", cfg, "net10.0", "sc-boost-agent.exe");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static async Task<(bool Success, string Message)> InstallAndStartStartupAgentAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "Startup registration is only supported on Windows.");
        }

        string? agentExe = FindBoostAgentExecutable();
        if (agentExe is null)
        {
            return (false, "Could not locate sc-boost-agent.exe. Build the solution with 'dotnet build -c Release' first.");
        }

        string commandValue = $"\"{agentExe}\"";
        using (var key = Registry.CurrentUser.CreateSubKey(RunRegistrySubKey, writable: true))
        {
            if (key is null)
            {
                return (false, $"Unable to open HKCU\\{RunRegistrySubKey} for writing.");
            }

            key.SetValue(RunValueName, commandValue, RegistryValueKind.String);
        }

        if (!LowLatencyBooster.IsBoosterRunning(out int? existingPid))
        {
            StartDetachedProcess(agentExe, string.Empty, Path.GetDirectoryName(agentExe)!);

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(3))
            {
                if (LowLatencyBooster.IsBoosterRunning(out existingPid))
                {
                    break;
                }

                await Task.Delay(100);
            }
        }

        string pidNote = existingPid.HasValue ? $"PID {existingPid.Value}" : "active";
        return (true, $"Registered windowless startup agent ({agentExe}) in HKCU\\...\\Run and verified running ({pidNote}).");
    }

    /// <summary>
    /// Launches an existing absolute executable detached from any parent console or Job Object
    /// (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) while preserving exact Win32 path quoting (preventing CWE-428)
    /// and avoiding any shell string interpolation (all payloads travel as UTF-16LE Base64 bytes).
    /// </summary>
    public static void StartDetachedProcess(string exePath, string arguments, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath) || !File.Exists(exePath))
        {
            throw new FileNotFoundException("Executable must be an existing absolute path.", exePath);
        }

        if (ContainsDisallowedControlChars(exePath) ||
            ContainsDisallowedControlChars(arguments) ||
            ContainsDisallowedControlChars(workingDirectory))
        {
            throw new ArgumentException("Process launch parameters must not contain control characters.");
        }

        string cleanExe = Path.GetFullPath(exePath);
        string cleanWorkDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(cleanExe)!
            : Path.GetFullPath(workingDirectory);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Always wrap the executable path in literal quotes for Win32_Process.Create (prevents CWE-428).
                // Pass both the quoted command line and working directory as Base64 byte payloads inside an
                // -EncodedCommand invocation so zero quote-stripping or PowerShell metacharacter parsing can occur,
                // while spawning via WmiPrvSE.exe breaks free of caller Job Objects (KILL_ON_JOB_CLOSE).
                string quotedCommandLine = string.IsNullOrWhiteSpace(arguments)
                    ? $"\"{cleanExe}\""
                    : $"\"{cleanExe}\" {arguments.Trim()}";

                string cmdB64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(quotedCommandLine));
                string wdB64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(cleanWorkDir));
                string psScript =
                    $"$c=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{cmdB64}'));" +
                    $"$w=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{wdB64}'));" +
                    "$r=Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine=$c;CurrentDirectory=$w};" +
                    "if($null -eq $r -or $r.ReturnValue -ne 0){exit 1}";
                string scriptB64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));

                using var wmiLauncher = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {scriptB64}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (wmiLauncher is not null && wmiLauncher.WaitForExit(5000) && wmiLauncher.ExitCode == 0)
                {
                    return;
                }
            }
            catch
            {
                // Fall back to direct ShellExecute below if WMI/PowerShell is unavailable.
            }
        }

        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = cleanExe,
            Arguments = arguments,
            WorkingDirectory = cleanWorkDir,
            UseShellExecute = true,
        });
    }

    private static bool ContainsDisallowedControlChars(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (char ch in value)
        {
            if (ch is '\0' or '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<(bool Success, string Message)> UninstallAndStopStartupAgentAsync(bool restoreNormalSteamPriority = true)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistrySubKey, writable: true);
                if (key?.GetValue(RunValueName) is not null)
                {
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                return (false, $"Failed to remove startup registry entry: {ex.Message}");
            }
        }

        bool stopped = await LowLatencyBooster.StopRunningBoosterAsync(TimeSpan.FromSeconds(4), restoreNormalSteamPriority);
        return stopped
            ? (true, "Removed HKCU\\...\\Run entry and stopped background Low-Latency Booster.")
            : (false, "Removed HKCU\\...\\Run entry, but background booster process is still exiting.");
    }
}
