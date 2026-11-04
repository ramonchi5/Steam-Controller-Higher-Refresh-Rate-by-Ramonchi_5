using System.Buffers.Binary;
using System.Globalization;

namespace SteamControllerRefreshRate.Firmware;

public sealed record Uf2ConversionSummary(
    int BlockCount,
    int OutputBytes,
    uint StartAddress,
    uint EndAddressExclusive,
    uint FamilyId);

/// <summary>
/// Byte-for-byte compatible implementation of Adafruit's uf2conv.py Intel HEX -> UF2 converter
/// (default family 0xADA52840 for Nordic nRF52840 UF2 bootloaders on SuperMini / Pro Micro / Nice!Nano).
/// </summary>
public static class Uf2Converter
{
    public const uint Uf2MagicStart0 = 0x0A324655u; // "UF2\n"
    public const uint Uf2MagicStart1 = 0x9E5D5157u;
    public const uint Uf2MagicEnd = 0x0AB16F30u;
    public const uint FlagFamilyIdPresent = 0x00002000u;
    public const uint Nrf52840FamilyId = 0xADA52840u;
    public const uint OpenPuckRfJournalBase = 0x00086000u;

    public static Uf2ConversionSummary ConvertHexFileToUf2(
        string hexPath,
        string uf2Path,
        uint familyId = Nrf52840FamilyId)
    {
        if (!File.Exists(hexPath))
        {
            throw new FileNotFoundException("Intel HEX file not found.", hexPath);
        }

        string[] lines = File.ReadAllLines(hexPath);
        uint upper = 0;
        uint minAddr = uint.MaxValue;
        uint maxAddrExclusive = 0;
        var pages = new SortedDictionary<uint, byte[]>();

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] != ':')
            {
                continue;
            }

            if ((line.Length - 1) % 2 != 0)
            {
                throw new InvalidDataException($"Malformed Intel HEX record length: '{line}'");
            }

            int byteCount = (line.Length - 1) / 2;
            if (byteCount < 5)
            {
                throw new InvalidDataException($"Malformed Intel HEX record: '{line}'");
            }

            byte[] rec = new byte[byteCount];
            byte checksum = 0;
            for (int i = 0; i < byteCount; i++)
            {
                rec[i] = byte.Parse(line.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                checksum = unchecked((byte)(checksum + rec[i]));
            }

            if (checksum != 0)
            {
                throw new InvalidDataException($"Intel HEX checksum mismatch in record: '{line}'");
            }

            int dataLen = rec[0];
            if (dataLen != byteCount - 5)
            {
                throw new InvalidDataException($"Intel HEX byte count mismatch in record: '{line}'");
            }

            byte recordType = rec[3];
            if (recordType == 0x04) // Extended Linear Address
            {
                if (dataLen != 2)
                {
                    throw new InvalidDataException($"Invalid Extended Linear Address record: '{line}'");
                }

                upper = (uint)((rec[4] << 8) | rec[5]) << 16;
            }
            else if (recordType == 0x02) // Extended Segment Address
            {
                if (dataLen != 2)
                {
                    throw new InvalidDataException($"Invalid Extended Segment Address record: '{line}'");
                }

                upper = (uint)((rec[4] << 8) | rec[5]) << 4;
            }
            else if (recordType == 0x01) // EOF
            {
                break;
            }
            else if (recordType == 0x00 && dataLen > 0) // Data
            {
                uint addr = upper | (uint)((rec[1] << 8) | rec[2]);
                if (addr < minAddr)
                {
                    minAddr = addr;
                }

                // Exclude trailing checksum byte at rec[byteCount - 1]
                for (int i = 4; i < 4 + dataLen; i++)
                {
                    uint aligned = addr & ~0xFFu;
                    if (!pages.TryGetValue(aligned, out byte[]? pageData))
                    {
                        pageData = new byte[256];
                        Array.Fill(pageData, (byte)0xFF);
                        pages[aligned] = pageData;
                    }

                    pageData[addr & 0xFFu] = rec[i];
                    addr++;
                    if (addr > maxAddrExclusive)
                    {
                        maxAddrExclusive = addr;
                    }
                }
            }
        }

        if (pages.Count == 0 || minAddr == uint.MaxValue)
        {
            throw new InvalidDataException($"No flashable data records found in '{hexPath}'.");
        }

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(uf2Path));
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        uint flags = familyId != 0 ? FlagFamilyIdPresent : 0u;
        uint numBlocks = (uint)pages.Count;
        byte[] sector = new byte[512];

        using (var fs = new FileStream(uf2Path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            uint blockIndex = 0;
            foreach (var (alignedAddress, pageData) in pages)
            {
                Array.Clear(sector);

                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0, 4), Uf2MagicStart0);
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(4, 4), Uf2MagicStart1);
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(8, 4), flags);
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(12, 4), alignedAddress);
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(16, 4), 256u);
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(20, 4), blockIndex++);
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(24, 4), numBlocks);
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(28, 4), familyId);

                Buffer.BlockCopy(pageData, 0, sector, 32, 256);
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(508, 4), Uf2MagicEnd);

                fs.Write(sector);
            }
        }

        return new Uf2ConversionSummary(
            BlockCount: pages.Count,
            OutputBytes: pages.Count * 512,
            StartAddress: minAddr,
            EndAddressExclusive: maxAddrExclusive,
            FamilyId: familyId);
    }
}
