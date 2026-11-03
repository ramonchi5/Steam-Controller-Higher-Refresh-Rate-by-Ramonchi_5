using System.Diagnostics;
using SteamControllerRefreshRate.Native;

namespace SteamControllerRefreshRate.Hid;

public sealed record InterfaceBenchmarkResult(
    ValveHidDeviceInfo Device,
    double DurationSeconds,
    int TotalReports,
    int StateReports,
    int AuxReports,
    int UniqueSeqFrames,
    int UniquePayloadFrames,
    int DuplicateSeqFrames,
    int MissedSeqGaps,
    byte LastReportId,
    double UsbReportHz,
    double StateReportHz,
    double UniqueSeqHz,
    double UniquePayloadHz,
    double MinDeltaMs,
    double AvgDeltaMs,
    double P50DeltaMs,
    double P95DeltaMs,
    double P99DeltaMs,
    double MaxDeltaMs,
    double JitterStdDevMs,
    string FirstSampleHex);

public static class PollingBenchmarker
{
    private const byte TritonStateQuat = 0x42;
    private const byte TritonStateNoQuat = 0x45;
    private const byte TritonStateTimestamp = 0x47;

    private static bool IsTritonStateReportId(byte id) =>
        id is TritonStateQuat or TritonStateNoQuat or TritonStateTimestamp;

    public static async Task<IReadOnlyList<InterfaceBenchmarkResult>> RunAsync(
        IReadOnlyList<ValveHidDeviceInfo> devices,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        // Filter to candidate raw controller interfaces (Triton wireless reports are 54B, wired are 64B; Usage 0xFF00:0x0001)
        var candidates = devices
            .Where(d => d.InputReportLength >= 48 && d.UsagePage != 0x0001 && !(d.UsagePage == 0xFF00 && d.Usage == 0x0002))
            .ToArray();

        if (candidates.Length == 0)
        {
            candidates = devices.Where(d => d.InputReportLength >= 32).ToArray();
        }

        if (candidates.Length == 0)
        {
            return Array.Empty<InterfaceBenchmarkResult>();
        }

        // Lock high-resolution Windows timer during measurement so cancellation and host scheduling are crisp
        WindowsSchedulerNative.TryLockMaxTimerResolution(out _, out uint locked100Ns, out bool usedWinMm);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(durationSeconds));

            var tasks = candidates
                .Select(dev => Task.Run(() => MeasureInterfaceAsync(dev, durationSeconds, cts.Token), CancellationToken.None))
                .ToArray();

            var results = await Task.WhenAll(tasks);
            return results
                .Where(r => r is not null)
                .Select(r => r!)
                .OrderByDescending(r => r.TotalReports)
                .ToArray();
        }
        finally
        {
            WindowsSchedulerNative.UnlockTimerResolution(locked100Ns, usedWinMm);
        }
    }

    private static async Task<InterfaceBenchmarkResult?> MeasureInterfaceAsync(
        ValveHidDeviceInfo dev,
        double requestedSeconds,
        CancellationToken token)
    {
        using var handle = SteamControllerScanner.OpenDeviceHandle(dev.DevicePath, out _);
        if (handle.IsInvalid)
        {
            return null;
        }

        // Expand kernel HID ring buffer to 512 reports so zero reports are dropped even at 1000-2000 Hz
        WindowsHidNative.HidD_SetNumInputBuffers(handle, 512);

        int reportLen = dev.InputReportLength > 0 ? dev.InputReportLength : 64;
        await using var stream = new FileStream(handle, FileAccess.Read, reportLen, isAsync: true);

        byte[] buffer = new byte[reportLen];
        byte[] prevStatePayload = new byte[reportLen];
        int prevStatePayloadLen = 0;
        byte[] prevNonStatePayload = new byte[reportLen];
        int prevNonStatePayloadLen = 0;
        bool hasPrevState = false;
        bool hasPrevNonState = false;
        byte prevSeq = 0;

        var stateTimestamps = new List<long>(capacity: (int)(requestedSeconds * 1500));
        var allTimestamps = new List<long>(capacity: (int)(requestedSeconds * 1500));
        int totalReports = 0;
        int stateReports = 0;
        int auxReports = 0;
        int uniqueSeqFrames = 0;
        int uniqueStatePayloadFrames = 0;
        int uniqueNonStateFrames = 0;
        int duplicateSeqFrames = 0;
        int missedSeqGaps = 0;
        byte lastReportId = 0;
        string firstHex = string.Empty;

        long startTick = Stopwatch.GetTimestamp();
        long firstReportTick = 0;
        long lastReportTick = 0;
        long firstStateTick = 0;
        long lastStateTick = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, reportLen), token);
                if (read <= 0)
                {
                    break;
                }

                long now = Stopwatch.GetTimestamp();
                if (totalReports == 0)
                {
                    firstReportTick = now;
                    firstHex = Convert.ToHexString(buffer.AsSpan(0, Math.Min(16, read)));
                }

                lastReportTick = now;
                totalReports++;
                allTimestamps.Add(now);

                bool isTritonState = IsTritonStateReportId(buffer[0]);
                // Also handle cases where Windows prepends a Report ID byte before 0x42/0x45/0x47
                int stateOffset = isTritonState ? 0 :
                    (read > 2 && IsTritonStateReportId(buffer[1]) ? 1 : -1);

                if (stateOffset >= 0)
                {
                    if (stateReports == 0)
                    {
                        firstStateTick = now;
                        firstHex = Convert.ToHexString(buffer.AsSpan(stateOffset, Math.Min(16, read - stateOffset)));
                    }

                    lastStateTick = now;
                    stateReports++;
                    lastReportId = buffer[stateOffset];
                    stateTimestamps.Add(now);

                    byte seq = buffer[stateOffset + 1];
                    int payloadLen = read - (stateOffset + 2);
                    ReadOnlySpan<byte> payload = buffer.AsSpan(stateOffset + 2, payloadLen);

                    if (!hasPrevState)
                    {
                        hasPrevState = true;
                        prevSeq = seq;
                        uniqueSeqFrames = 1;
                        uniqueStatePayloadFrames = 1;
                        payload.CopyTo(prevStatePayload);
                        prevStatePayloadLen = payloadLen;
                    }
                    else
                    {
                        byte seqDiff = unchecked((byte)(seq - prevSeq));
                        if (seqDiff == 0)
                        {
                            duplicateSeqFrames++;
                        }
                        else
                        {
                            uniqueSeqFrames++;
                            if (seqDiff > 1)
                            {
                                missedSeqGaps += seqDiff - 1;
                            }
                        }

                        if (prevStatePayloadLen != payloadLen ||
                            !payload.SequenceEqual(prevStatePayload.AsSpan(0, prevStatePayloadLen)))
                        {
                            uniqueStatePayloadFrames++;
                        }

                        prevSeq = seq;
                        payload.CopyTo(prevStatePayload);
                        prevStatePayloadLen = payloadLen;
                    }
                }
                else
                {
                    auxReports++;
                    if (stateReports == 0)
                    {
                        lastReportId = buffer[0];
                    }

                    if (!hasPrevNonState)
                    {
                        hasPrevNonState = true;
                        uniqueNonStateFrames = 1;
                        buffer.AsSpan(0, read).CopyTo(prevNonStatePayload);
                        prevNonStatePayloadLen = read;
                    }
                    else if (prevNonStatePayloadLen != read ||
                             !buffer.AsSpan(0, read).SequenceEqual(prevNonStatePayload.AsSpan(0, prevNonStatePayloadLen)))
                    {
                        uniqueNonStateFrames++;
                        buffer.AsSpan(0, read).CopyTo(prevNonStatePayload);
                        prevNonStatePayloadLen = read;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }

        long endTick = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        double spanSeconds = totalReports >= 2
            ? (lastReportTick - firstReportTick) / freq
            : (endTick - startTick) / freq;

        if (spanSeconds <= 0.001)
        {
            spanSeconds = requestedSeconds;
        }

        double stateSpanSeconds = stateReports >= 2
            ? (lastStateTick - firstStateTick) / freq
            : spanSeconds;
        if (stateSpanSeconds <= 0.001)
        {
            stateSpanSeconds = spanSeconds;
        }

        int uniquePayloadFrames = stateReports > 0 ? uniqueStatePayloadFrames : uniqueNonStateFrames;
        double usbHz = totalReports >= 2 ? (totalReports - 1) / spanSeconds : 0;
        double stateHz = stateReports >= 2 ? (stateReports - 1) / stateSpanSeconds : 0;
        double uniqueSeqHz = uniqueSeqFrames >= 2 ? (uniqueSeqFrames - 1) / stateSpanSeconds : 0;
        double uniquePayloadHz = uniquePayloadFrames >= 2
            ? (uniquePayloadFrames - 1) / (stateReports > 0 ? stateSpanSeconds : spanSeconds)
            : 0;

        var timingSource = stateTimestamps.Count >= 2 ? stateTimestamps : allTimestamps;
        double minMs = 0, avgMs = 0, p50Ms = 0, p95Ms = 0, p99Ms = 0, maxMs = 0, stdMs = 0;
        if (timingSource.Count >= 2)
        {
            double[] deltasMs = new double[timingSource.Count - 1];
            double sum = 0;
            for (int i = 0; i < deltasMs.Length; i++)
            {
                double d = (timingSource[i + 1] - timingSource[i]) * 1000.0 / freq;
                deltasMs[i] = d;
                sum += d;
            }

            avgMs = sum / deltasMs.Length;
            double sqSum = 0;
            for (int i = 0; i < deltasMs.Length; i++)
            {
                double diff = deltasMs[i] - avgMs;
                sqSum += diff * diff;
            }

            stdMs = Math.Sqrt(sqSum / deltasMs.Length);
            Array.Sort(deltasMs);
            minMs = deltasMs[0];
            p50Ms = Percentile(deltasMs, 0.50);
            p95Ms = Percentile(deltasMs, 0.95);
            p99Ms = Percentile(deltasMs, 0.99);
            maxMs = deltasMs[^1];
        }

        return new InterfaceBenchmarkResult(
            dev,
            spanSeconds,
            totalReports,
            stateReports,
            auxReports,
            uniqueSeqFrames,
            uniquePayloadFrames,
            duplicateSeqFrames,
            missedSeqGaps,
            lastReportId,
            usbHz,
            stateHz,
            uniqueSeqHz,
            uniquePayloadHz,
            minMs,
            avgMs,
            p50Ms,
            p95Ms,
            p99Ms,
            maxMs,
            stdMs,
            firstHex);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        double idx = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }
}
