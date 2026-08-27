using System;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Config;

public static class PreRecordBufferPolicy
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;
    private const int MemoryStepMb = 64;

    public static int GetRecommendedDefaultMb(ulong physicalMemoryBytes)
    {
        long tiers = GetMemoryTier(physicalMemoryBytes);
        return (int)Math.Clamp(Math.Max(0, tiers - 1) * MemoryStepMb, 0, 1024);
    }

    public static int GetRamMaximumMb(ulong physicalMemoryBytes)
    {
        long tiers = GetMemoryTier(physicalMemoryBytes);
        return (int)Math.Clamp(tiers * 512L, 0, int.MaxValue);
    }

    public static int GetMaximumMb(int width, int height, int fps, ulong physicalMemoryBytes)
    {
        int ramMaximum = GetRamMaximumMb(physicalMemoryBytes);
        if (width <= 0 || height <= 0 || fps <= 0)
            return ramMaximum;

        double fiveSecondBytes = (double)width * height * 3 * fps * 5;
        long fiveSecondMb = (long)Math.Ceiling(fiveSecondBytes / MiB);
        long roundedFiveSecondMb = ((fiveSecondMb + MemoryStepMb - 1) / MemoryStepMb) * MemoryStepMb;
        return (int)Math.Min(ramMaximum, roundedFiveSecondMb);
    }

    public static int ClampConfiguredMb(int configuredMb, int width, int height, int fps, ulong physicalMemoryBytes) =>
        Math.Clamp(configuredMb, 0, GetMaximumMb(width, height, fps, physicalMemoryBytes));

    public static ulong GetPhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhysical : 0;
    }

    private static long GetMemoryTier(ulong physicalMemoryBytes)
    {
        double memoryGiB = physicalMemoryBytes / (double)GiB;
        if (memoryGiB < 6)
            return 0;

        // Windows reports nominal 8GB/16GB/32GB machines slightly below their advertised capacity.
        // Round to the nearest 8GB tier so hardware-reserved memory does not drop a user one tier.
        return Math.Max(1, (long)Math.Round(memoryGiB / 8d, MidpointRounding.AwayFromZero));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
