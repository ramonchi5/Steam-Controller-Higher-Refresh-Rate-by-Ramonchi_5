using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SteamControllerRefreshRate.Native;

namespace SteamControllerRefreshRate.Hid;

public sealed record ValveHidDeviceInfo(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    ushort VersionNumber,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int FeatureReportLength,
    string? ProductName,
    string? SerialNumber,
    string RoleDescription,
    ushort? ReportedAttrProductId,
    uint? FirmwareTimestamp,
    int? BatteryPercent,
    bool CanWrite);

public static class SteamControllerScanner
{
    public const ushort ValveVendorId = 0x28DE;
    public const ushort IbexWiredPid = 0x1302;
    public const ushort ProteusPuckPid = 0x1304;
    public const ushort SteamMachineReceiverPid = 0x1305;
    public const byte CmdGetAttributesValues = 0x83;
    public const byte CmdSetSettingsValues = 0x87;
    public const byte CmdGetWirelessState = 0xB4;
    public const byte SettingLizardMode = 9;

    public static IReadOnlyList<ValveHidDeviceInfo> ScanDevices()
    {
        var list = new List<ValveHidDeviceInfo>();
        if (!OperatingSystem.IsWindows())
        {
            return list;
        }

        foreach (string path in EnumerateValveHidPaths())
        {
            using var handle = OpenDeviceHandle(path, out bool canWrite);
            if (handle.IsInvalid)
            {
                continue;
            }

            var attrs = new WindowsHidNative.HiddAttributes
            {
                Size = Marshal.SizeOf<WindowsHidNative.HiddAttributes>(),
            };
            WindowsHidNative.HidD_GetAttributes(handle, ref attrs);

            ushort usagePage = 0, usage = 0;
            int inputLen = 64, featureLen = 64;
            if (WindowsHidNative.HidD_GetPreparsedData(handle, out IntPtr preparsed))
            {
                try
                {
                    if (WindowsHidNative.HidP_GetCaps(preparsed, out var caps) == WindowsHidNative.HidpStatusSuccess)
                    {
                        usagePage = caps.UsagePage;
                        usage = caps.Usage;
                        inputLen = caps.InputReportByteLength;
                        featureLen = caps.FeatureReportByteLength;
                    }
                }
                finally
                {
                    WindowsHidNative.HidD_FreePreparsedData(preparsed);
                }
            }

            string? product = WindowsHidNative.ReadUnicodeString(
                (buf, len) => WindowsHidNative.HidD_GetProductString(handle, buf, len));
            string? serial = WindowsHidNative.ReadUnicodeString(
                (buf, len) => WindowsHidNative.HidD_GetSerialNumberString(handle, buf, len));

            ushort? attrPid = null;
            uint? fwTs = null;
            int? batteryPct = null;
            if (canWrite && featureLen >= 32 && usagePage >= 0xFF00)
            {
                TryQueryAttributes(handle, featureLen, usage, out attrPid, out fwTs, out batteryPct);
            }

            string role = ClassifyDevice(path, attrs.ProductID, usagePage, usage, inputLen, featureLen);

            list.Add(new ValveHidDeviceInfo(
                path,
                attrs.VendorID,
                attrs.ProductID,
                attrs.VersionNumber,
                usagePage,
                usage,
                inputLen,
                featureLen,
                product,
                serial,
                role,
                attrPid,
                fwTs,
                batteryPct,
                canWrite));
        }

        return list;
    }

    public static SafeFileHandle OpenDeviceHandle(string path, out bool canWrite)
    {
        canWrite = true;
        var handle = WindowsHidNative.CreateFile(
            path,
            WindowsHidNative.GenericRead | WindowsHidNative.GenericWrite,
            WindowsHidNative.FileShareRead | WindowsHidNative.FileShareWrite,
            IntPtr.Zero,
            WindowsHidNative.OpenExisting,
            WindowsHidNative.FileFlagOverlapped,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            canWrite = false;
            handle.Dispose();
            handle = WindowsHidNative.CreateFile(
                path,
                WindowsHidNative.GenericRead,
                WindowsHidNative.FileShareRead | WindowsHidNative.FileShareWrite,
                IntPtr.Zero,
                WindowsHidNative.OpenExisting,
                WindowsHidNative.FileFlagOverlapped,
                IntPtr.Zero);
        }

        return handle;
    }

    public static bool TryDisableLizardMode(SafeFileHandle handle, int featureReportLen = 64)
    {
        int len = Math.Max(64, featureReportLen);
        byte[] report = new byte[len];
        report[0] = 0x01; // Report ID 1 (Triton / Proteus vendor collection)
        report[1] = CmdSetSettingsValues;
        report[2] = 3;
        report[3] = SettingLizardMode;
        report[4] = 0;
        report[5] = 0;
        return WindowsHidNative.HidD_SetFeature(handle, report, report.Length);
    }

    private static bool TryQueryAttributes(
        SafeFileHandle handle,
        int featureLen,
        ushort usage,
        out ushort? reportedProductId,
        out uint? fwTimestamp,
        out int? batteryPercent)
    {
        reportedProductId = null;
        fwTimestamp = null;
        batteryPercent = null;

        byte[] candidateReportIds = usage == 0x0002
            ? new byte[] { 0x02 }
            : new byte[] { 0x01, 0x00 };

        foreach (byte reportId in candidateReportIds)
        {
            int len = Math.Max(64, featureLen);
            byte[] req = new byte[len];
            req[0] = reportId;
            req[1] = CmdGetAttributesValues;
            req[2] = 0;

            if (!WindowsHidNative.HidD_SetFeature(handle, req, req.Length))
            {
                continue;
            }

            byte[] resp = new byte[len];
            resp[0] = reportId;
            if (!WindowsHidNative.HidD_GetFeature(handle, resp, resp.Length))
            {
                continue;
            }

            int baseOff = resp[1] == CmdGetAttributesValues ? 1 : (resp[0] == CmdGetAttributesValues ? 0 : -1);
            if (baseOff < 0 || baseOff + 2 >= resp.Length)
            {
                continue;
            }

            int payloadBytes = resp[baseOff + 1];
            int cursor = baseOff + 2;
            int end = Math.Min(resp.Length, cursor + payloadBytes);
            while (cursor + 5 <= end)
            {
                byte tag = resp[cursor];
                uint val = BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(cursor + 1, 4));
                if (tag == 1 && val <= ushort.MaxValue && val != 0)
                {
                    reportedProductId = (ushort)val;
                }
                else if (tag == 4 && val != 0)
                {
                    fwTimestamp = val;
                }
                else if (tag == 11 && val <= 100)
                {
                    batteryPercent = (int)val;
                }

                cursor += 5;
            }

            return true;
        }

        return false;
    }

    private static string ClassifyDevice(
        string path,
        ushort pid,
        ushort usagePage,
        ushort usage,
        int inputLen,
        int featureLen)
    {
        if (usagePage == 0x0001 && usage == 0x0002)
        {
            return "Lizard Mode Mouse HID";
        }

        if (usagePage == 0x0001 && usage == 0x0006)
        {
            return "Lizard Mode Keyboard HID";
        }

        if (pid is ProteusPuckPid or SteamMachineReceiverPid)
        {
            if (usagePage == 0xFF00 && usage == 0x0002)
            {
                return "Proteus Puck Management (hid-puck, Usage 0xFF00:0x0002, 32ms)";
            }

            // MI_02..MI_05 are Wireless Controller Slots 0..3 (Usage 0xFF00:0x0001; stock Puck=500Hz USB, OpenPuck=1000Hz USB)
            for (int slot = 0; slot < 4; slot++)
            {
                if (path.Contains($"mi_0{slot + 2}", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Wireless Controller Slot {slot} (HID_PROXY_{slot})";
                }
            }

            return inputLen >= 48
                ? "Wireless Controller Raw HID Interface"
                : "Wireless Receiver Auxiliary HID";
        }

        if (pid == IbexWiredPid)
        {
            return inputLen >= 48
                ? "Steam Controller Gen2 Wired (Ibex/Triton, USB 1000Hz)"
                : "Steam Controller Gen2 Auxiliary HID";
        }

        return inputLen >= 48
            ? $"Valve Raw Controller HID (PID 0x{pid:X4})"
            : $"Valve HID Interface (PID 0x{pid:X4})";
    }

    public static IEnumerable<string> EnumerateValveHidPaths()
    {
        WindowsHidNative.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceInfoSet = WindowsHidNative.SetupDiGetClassDevs(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            WindowsHidNative.DigcfPresent | WindowsHidNative.DigcfDeviceInterface);

        if (deviceInfoSet == new IntPtr(WindowsHidNative.InvalidHandleValue))
        {
            yield break;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new WindowsHidNative.SpDeviceInterfaceData
                {
                    CbSize = Marshal.SizeOf<WindowsHidNative.SpDeviceInterfaceData>(),
                };

                if (!WindowsHidNative.SetupDiEnumDeviceInterfaces(
                        deviceInfoSet,
                        IntPtr.Zero,
                        ref hidGuid,
                        index,
                        ref interfaceData))
                {
                    yield break;
                }

                WindowsHidNative.SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet,
                    ref interfaceData,
                    IntPtr.Zero,
                    0,
                    out int requiredSize,
                    IntPtr.Zero);

                if (requiredSize <= 0)
                {
                    continue;
                }

                IntPtr detailData = Marshal.AllocHGlobal(requiredSize);
                try
                {
                    Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);
                    if (!WindowsHidNative.SetupDiGetDeviceInterfaceDetail(
                            deviceInfoSet,
                            ref interfaceData,
                            detailData,
                            requiredSize,
                            out _,
                            IntPtr.Zero))
                    {
                        continue;
                    }

                    string? path = ReadDevicePath(detailData);
                    if (!string.IsNullOrEmpty(path) &&
                        path.Contains($"vid_{ValveVendorId:x4}", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return path;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailData);
                }
            }
        }
        finally
        {
            WindowsHidNative.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string? ReadDevicePath(IntPtr detailData)
    {
        foreach (int offset in new[] { 4, 8, 6 })
        {
            string? path = Marshal.PtrToStringUni(IntPtr.Add(detailData, offset));
            if (!string.IsNullOrWhiteSpace(path) && path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }
}
