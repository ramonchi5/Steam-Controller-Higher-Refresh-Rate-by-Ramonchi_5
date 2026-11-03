using SteamControllerRefreshRate.Boost;

bool suppressLizard = args.Any(a => a.Equals("--lizard-off", StringComparison.OrdinalIgnoreCase));
await LowLatencyBooster.RunDaemonAsync(suppressLizardMode: suppressLizard, CancellationToken.None, quiet: true);
return 0;
