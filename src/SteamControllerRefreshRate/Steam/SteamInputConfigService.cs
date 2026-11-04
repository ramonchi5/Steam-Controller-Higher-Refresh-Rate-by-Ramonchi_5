using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SteamControllerRefreshRate.Boost;

namespace SteamControllerRefreshRate.Steam;

public sealed record UserLocalConfigStatus(
    string AccountId,
    string FilePath,
    int? ConfiguredPollRateMs,
    int EffectiveSteamInputHz);

public sealed record SteamFirmwareTargets(
    string ConfigPath,
    uint? TritonControllerFwTimestamp,
    uint? ProteusPuckFwTimestamp,
    uint? MustUpdateTritonFwTimestamp,
    uint? MustUpdateProteusFwTimestamp);

public static class SteamInputConfigService
{
    public const string PollRateVdfKey = "SteamController_Poll_Rate";

    public static string? FindSteamInstallPath()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string regPath && !string.IsNullOrWhiteSpace(regPath))
                {
                    string normalized = Path.GetFullPath(regPath.Replace('/', '\\'));
                    if (Directory.Exists(normalized))
                    {
                        return normalized;
                    }
                }
            }
            catch
            {
            }
        }

        foreach (string candidate in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam",
                 })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static IReadOnlyList<UserLocalConfigStatus> InspectLocalConfigs(string steamPath)
    {
        var results = new List<UserLocalConfigStatus>();
        string userdataDir = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdataDir))
        {
            return results;
        }

        foreach (string userDir in Directory.GetDirectories(userdataDir))
        {
            string accountId = Path.GetFileName(userDir);
            if (!accountId.All(char.IsDigit))
            {
                continue;
            }

            string localConfigPath = Path.Combine(userDir, "config", "localconfig.vdf");
            if (!File.Exists(localConfigPath))
            {
                continue;
            }

            string vdf = File.ReadAllText(localConfigPath, Encoding.UTF8);
            int? ms = TryReadPollRateMs(vdf);
            int effectiveMs = ms ?? 2; // Valve default in steamclient64.dll is 2 (2000us = 500Hz)
            int effectiveHz = effectiveMs > 0 ? 1000 / effectiveMs : 0;

            results.Add(new UserLocalConfigStatus(accountId, localConfigPath, ms, effectiveHz));
        }

        return results;
    }

    public static SteamFirmwareTargets? TryReadHardwareUpdaterTargets(string steamPath)
    {
        string cfgPath = Path.Combine(steamPath, "bin", "hardwareupdater", "hardwareupdater.cfg");
        if (!File.Exists(cfgPath))
        {
            return null;
        }

        uint? tritonTs = null;
        uint? proteusTs = null;
        uint? mustTritonTs = null;
        uint? mustProteusTs = null;

        foreach (string rawLine in File.ReadAllLines(cfgPath, Encoding.UTF8))
        {
            string line = rawLine.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0 || colon + 1 >= line.Length)
            {
                continue;
            }

            string k = line[..colon].Trim();
            string v = line[(colon + 1)..].Trim();
            if (!uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint ts))
            {
                continue;
            }

            switch (k)
            {
                case "TRITON_FW_TS":
                    tritonTs = ts;
                    break;
                case "PROTEUS_FW_TS":
                    proteusTs = ts;
                    break;
                case "MUST_UPDATE_TRITON_FW_TS":
                    mustTritonTs = ts;
                    break;
                case "MUST_UPDATE_PROTEUS_FW_TS":
                    mustProteusTs = ts;
                    break;
            }
        }

        return new SteamFirmwareTargets(cfgPath, tritonTs, proteusTs, mustTritonTs, mustProteusTs);
    }

    public static bool IsSteamRunning()
    {
        if (!IsSteamRunning(out var procs))
        {
            return false;
        }

        foreach (var p in procs)
        {
            p.Dispose();
        }

        return true;
    }

    public static bool IsSteamRunning(out Process[] steamProcesses)
    {
        try
        {
            steamProcesses = Process.GetProcessesByName("steam");
            return steamProcesses.Length > 0;
        }
        catch
        {
            steamProcesses = Array.Empty<Process>();
            return false;
        }
    }

    public static async Task<bool> ShutdownSteamGracefullyAsync(string steamPath, TimeSpan timeout)
    {
        if (!IsSteamRunning())
        {
            return true;
        }

        string steamExe = Path.Combine(steamPath, "steam.exe");
        if (File.Exists(steamExe))
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                Arguments = "-shutdown",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!IsSteamRunning())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return !IsSteamRunning();
    }

    public static void LaunchSteam(string steamPath, int? controllerRateUs = null)
    {
        string steamExe = Path.Combine(steamPath, "steam.exe");
        if (!File.Exists(steamExe))
        {
            throw new FileNotFoundException("steam.exe not found", steamExe);
        }

        string args = controllerRateUs.HasValue
            ? $"+controller_rate {controllerRateUs.Value}"
            : string.Empty;

        StartupRegistrationService.StartDetachedProcess(steamExe, args, steamPath);
    }

    public static IReadOnlyList<string> ApplyPollRateMsToAllUsers(string steamPath, int pollRateMs)
    {
        if (pollRateMs is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollRateMs),
                "SteamController_Poll_Rate must be >= 1 ms (1 = 1000 Hz, 2 = 500 Hz default). Never use 0: steamclient64.dll multiplies this value by 1000 us and uses it as the gyro/mouse filter timestep!");
        }

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var modified = new List<string>();
        foreach (var cfg in InspectLocalConfigs(steamPath))
        {
            string original = File.ReadAllText(cfg.FilePath, Encoding.UTF8);
            string updated = UpsertRootKeyInUserLocalConfigStore(original, PollRateVdfKey, pollRateMs.ToString());
            if (string.Equals(original, updated, StringComparison.Ordinal))
            {
                modified.Add(cfg.FilePath);
                continue;
            }

            string backupPath = cfg.FilePath + $".bak.{DateTime.Now:yyyyMMdd-HHmmss}";
            File.WriteAllText(backupPath, original, utf8NoBom);
            PruneOldBackups(cfg.FilePath, maxBackupsToKeep: 5);

            // Atomic write via sibling temp file + rename so power loss/interruption never truncates localconfig.vdf
            string tempPath = cfg.FilePath + $".tmp.{Environment.ProcessId}";
            try
            {
                File.WriteAllText(tempPath, updated, utf8NoBom);
                File.Move(tempPath, cfg.FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }

            modified.Add(cfg.FilePath);
        }

        return modified;
    }

    private static void PruneOldBackups(string vdfFilePath, int maxBackupsToKeep)
    {
        try
        {
            string? dir = Path.GetDirectoryName(vdfFilePath);
            string fileName = Path.GetFileName(vdfFilePath);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            var backups = new DirectoryInfo(dir)
                .GetFiles($"{fileName}.bak.*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(maxBackupsToKeep)
                .ToArray();

            foreach (var old in backups)
            {
                try { old.Delete(); } catch { }
            }
        }
        catch
        {
        }
    }

    public static int? TryReadPollRateMs(string vdfContent)
    {
        // steamclient64.dll reads IClientConfigStore::GetInt(k_EConfigStoreUserLocal, "SteamController_Poll_Rate", 2)
        // strictly from the root of "UserLocalConfigStore" (single-tab indent at depth 1).
        var match = Regex.Match(
            vdfContent,
            $"^\\t\"{Regex.Escape(PollRateVdfKey)}\"\\s+\"(-?\\d+)\"\\s*\\r?$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        if (match.Success && int.TryParse(match.Groups[1].Value, out int val))
        {
            return val;
        }

        return null;
    }

    private static string UpsertRootKeyInUserLocalConfigStore(string vdfContent, string key, string value)
    {
        // Remove any accidentally nested duplicates (depth >= 2 tabs, e.g. inside "System") so only the valid root key remains
        string nestedPattern = $"^\\t{{2,}}\"{Regex.Escape(key)}\"\\s+\"[^\"]*\"\\s*\\r?\\n";
        string cleaned = Regex.Replace(vdfContent, nestedPattern, string.Empty, RegexOptions.Multiline | RegexOptions.CultureInvariant);

        string rootPattern = $"^(\\t\"{Regex.Escape(key)}\"\\s+\")(-?\\d+)(\"\\s*\\r?)$";
        if (Regex.IsMatch(cleaned, rootPattern, RegexOptions.Multiline | RegexOptions.CultureInvariant))
        {
            return Regex.Replace(
                cleaned,
                rootPattern,
                m => $"{m.Groups[1].Value}{value}{m.Groups[3].Value}",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
        }

        // Insert as a top-level key inside "UserLocalConfigStore" { ... } right before the final closing brace
        string newline = cleaned.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        int lastBrace = cleaned.LastIndexOf('}');
        if (lastBrace < 0)
        {
            throw new InvalidOperationException("Malformed localconfig.vdf: missing closing brace.");
        }

        string entry = $"\t\"{key}\"\t\t\"{value}\"{newline}";
        return cleaned.Insert(lastBrace, entry);
    }
}
