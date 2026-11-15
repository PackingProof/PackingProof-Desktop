namespace ExpressPackingMonitoring.UpdateCore;

/// <summary>
/// 读取并关闭 PE 调试目录里的 CET_COMPAT 标记。
///
/// .NET 10 在 Windows 影子栈已启用时要求系统完整支持 special user-mode APC；部分 Windows 10
/// 版本只开启了影子栈却没有该能力，CLR 会在应用代码运行前以 COR_E_EXECUTIONENGINE 退出。
/// 关闭 apphost 的 CET_COMPAT 可让 Windows 不为此进程开启影子栈，CFG 等其他缓解不变。
/// </summary>
public static class PortableExecutableCetPolicy
{
    private const uint PeSignature = 0x00004550;
    private const ushort Pe32Magic = 0x010B;
    private const ushort Pe32PlusMagic = 0x020B;
    private const int DebugDirectoryIndex = 6;
    private const int DebugDirectoryEntrySize = 28;
    private const uint ExtendedDllCharacteristicsDebugType = 20;
    private const uint CetCompatibleFlag = 0x00000001;

    public static bool IsCetCompatibilityEnabled(string executablePath)
    {
        using FileStream stream = OpenRead(executablePath);
        return TryFindCetFlagsOffset(stream, out long flagsOffset, out uint flags)
            && (flags & CetCompatibleFlag) != 0
            && flagsOffset > 0;
    }

    public static bool TryDisableCetCompatibility(
        string executablePath,
        out bool changed,
        out string? error)
    {
        changed = false;
        error = null;
        try
        {
            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            if (!TryFindCetFlagsOffset(stream, out long flagsOffset, out uint flags))
                return true;
            if ((flags & CetCompatibleFlag) == 0)
                return true;

            stream.Position = flagsOffset;
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                writer.Write(flags & ~CetCompatibleFlag);
            UpdatePeChecksum(stream);
            changed = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException
                                   or OverflowException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static FileStream OpenRead(string executablePath) =>
        new(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static bool TryFindCetFlagsOffset(
        FileStream stream,
        out long flagsOffset,
        out uint flags)
    {
        flagsOffset = 0;
        flags = 0;
        if (stream.Length < 0x40) return false;

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        if (reader.ReadUInt16() != 0x5A4D) return false;

        stream.Position = 0x3C;
        uint peOffset = reader.ReadUInt32();
        if (peOffset > stream.Length - 24) return false;
        stream.Position = peOffset;
        if (reader.ReadUInt32() != PeSignature) return false;

        stream.Position = peOffset + 6;
        ushort sectionCount = reader.ReadUInt16();
        stream.Position = peOffset + 20;
        ushort optionalHeaderSize = reader.ReadUInt16();
        long optionalHeaderOffset = peOffset + 24L;
        if (optionalHeaderOffset > stream.Length - Math.Min((int)optionalHeaderSize, 64))
            return false;

        stream.Position = optionalHeaderOffset;
        ushort optionalMagic = reader.ReadUInt16();
        int dataDirectoryOffset = optionalMagic switch
        {
            Pe32Magic => 96,
            Pe32PlusMagic => 112,
            _ => -1
        };
        if (dataDirectoryOffset < 0 || optionalHeaderSize < dataDirectoryOffset + 8) return false;

        stream.Position = optionalHeaderOffset + (optionalMagic == Pe32Magic ? 92 : 108);
        uint dataDirectoryCount = reader.ReadUInt32();
        if (dataDirectoryCount <= DebugDirectoryIndex) return false;

        stream.Position = optionalHeaderOffset + dataDirectoryOffset + DebugDirectoryIndex * 8;
        uint debugRva = reader.ReadUInt32();
        uint debugSize = reader.ReadUInt32();
        if (debugRva == 0 || debugSize < DebugDirectoryEntrySize) return false;

        long sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
        if (sectionTableOffset < optionalHeaderOffset
            || sectionTableOffset > stream.Length - sectionCount * 40L)
        {
            return false;
        }

        var sections = new List<PeSection>(sectionCount);
        for (int index = 0; index < sectionCount; index++)
        {
            stream.Position = sectionTableOffset + index * 40L + 8;
            uint virtualSize = reader.ReadUInt32();
            uint virtualAddress = reader.ReadUInt32();
            uint rawSize = reader.ReadUInt32();
            uint rawPointer = reader.ReadUInt32();
            sections.Add(new PeSection(virtualAddress, virtualSize, rawSize, rawPointer));
        }

        long debugDirectoryOffset = RvaToFileOffset(debugRva, sections);
        if (debugDirectoryOffset < 0
            || debugDirectoryOffset > stream.Length - debugSize)
        {
            return false;
        }

        for (long offset = debugDirectoryOffset;
             offset <= debugDirectoryOffset + debugSize - DebugDirectoryEntrySize;
             offset += DebugDirectoryEntrySize)
        {
            stream.Position = offset;
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt16();
            reader.ReadUInt16();
            uint type = reader.ReadUInt32();
            uint dataSize = reader.ReadUInt32();
            uint dataRva = reader.ReadUInt32();
            uint dataPointer = reader.ReadUInt32();
            if (type != ExtendedDllCharacteristicsDebugType || dataSize < sizeof(uint))
                continue;

            long candidateOffset = dataPointer != 0
                ? dataPointer
                : RvaToFileOffset(dataRva, sections);
            if (candidateOffset < 0 || candidateOffset > stream.Length - sizeof(uint))
                continue;

            stream.Position = candidateOffset;
            flagsOffset = candidateOffset;
            flags = reader.ReadUInt32();
            return true;
        }

        return false;
    }

    private static long RvaToFileOffset(uint rva, IReadOnlyList<PeSection> sections)
    {
        foreach (PeSection section in sections)
        {
            uint span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + span)
                continue;
            return section.RawPointer + (long)rva - section.VirtualAddress;
        }

        return -1;
    }

    private static void UpdatePeChecksum(FileStream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 0x3C;
        uint peOffset = reader.ReadUInt32();
        long optionalHeaderOffset = peOffset + 24L;
        long checksumOffset = optionalHeaderOffset + 64;
        if (checksumOffset < 0 || checksumOffset > stream.Length - sizeof(uint))
            return;

        uint checksum = 0;
        stream.Position = 0;
        for (long offset = 0; offset < stream.Length; offset += 2)
        {
            ushort word = 0;
            if (offset < checksumOffset || offset >= checksumOffset + sizeof(uint))
            {
                int first = stream.ReadByte();
                int second = offset + 1 < stream.Length ? stream.ReadByte() : 0;
                if (first < 0 || second < 0) break;
                word = (ushort)(first | (second << 8));
            }
            else
            {
                stream.Position += Math.Min(2, stream.Length - stream.Position);
            }

            checksum += word;
            checksum = (checksum & 0xFFFF) + (checksum >> 16);
        }

        checksum = (checksum & 0xFFFF) + (checksum >> 16);
        checksum += (uint)stream.Length;
        stream.Position = checksumOffset;
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(checksum);
    }

    private readonly record struct PeSection(
        uint VirtualAddress,
        uint VirtualSize,
        uint RawSize,
        uint RawPointer);
}
