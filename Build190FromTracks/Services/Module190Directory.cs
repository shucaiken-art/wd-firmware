using System.Text;

namespace Build190FromTracks.Services;

/// <summary>
/// Parses module 0x0190 allocation from WD SA directory module 01.rpm (20-byte entries).
/// </summary>
public static class Module190Directory
{
    public const ushort ModuleId = 0x0190;
    public const byte ExpectedRegionMask = 0x0C;
    public const byte ExpectedCopyCount = 2;

    public sealed record EntryInfo(
        int EntryOffset,
        uint SectorCount,
        uint Attributes,
        uint Address0,
        uint Address1,
        long ByteLength);

    public static EntryInfo ParseFrom01Rpm(string path01Rpm)
    {
        var directory = File.ReadAllBytes(path01Rpm);
        if (directory.Length < 52)
            throw new InvalidDataException("01.rpm too small.");

        ushort count = BitConverter.ToUInt16(directory, 48);
        int offset = 50;
        var matches = new List<int>();

        for (int i = 0; i < count; i++)
        {
            if (offset + 20 > directory.Length)
                throw new InvalidDataException($"Directory truncated at entry {i}.");
            if (directory[offset] != 20)
                throw new InvalidDataException($"Unexpected entry stride {directory[offset]} at 0x{offset:X}.");

            ushort id = BitConverter.ToUInt16(directory, offset + 2);
            if (id == ModuleId)
                matches.Add(offset);

            offset += directory[offset];
        }

        if (matches.Count == 0)
            throw new InvalidDataException("No directory entry for module 0x0190.");
        if (matches.Count > 1)
            throw new InvalidDataException($"Multiple directory entries for module 0x0190 ({matches.Count}).");

        int entry = matches[0];
        byte copyCount = directory[entry + 1];
        uint sectors = BitConverter.ToUInt32(directory, entry + 4);
        uint attributes = BitConverter.ToUInt32(directory, entry + 8);
        uint addr0 = BitConverter.ToUInt32(directory, entry + 12);
        uint addr1 = BitConverter.ToUInt32(directory, entry + 16);

        if (copyCount != ExpectedCopyCount)
            throw new InvalidDataException($"Expected {ExpectedCopyCount} copies, got {copyCount}.");
        if ((attributes & 0xFF) != ExpectedRegionMask)
            throw new InvalidDataException(
                $"Module 190 region mask is 0x{attributes & 0xFF:X2} (expected 0x{ExpectedRegionMask:X2} → SA regions 2 and 3).");

        if (sectors == 0)
            throw new InvalidDataException("Module 190 sector count is zero.");

        return new EntryInfo(entry, sectors, attributes, addr0, addr1, (long)sectors * 512);
    }

    public static string Describe(EntryInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Directory entry offset: 0x{info.EntryOffset:X}");
        sb.AppendLine($"Allocation: {info.SectorCount} sectors ({info.ByteLength:N0} bytes)");
        sb.AppendLine($"Attributes: 0x{info.Attributes:X8} (low byte = region bitmap)");
        sb.AppendLine($"Region-relative addresses: {info.Address0}, {info.Address1}");
        return sb.ToString();
    }
}
