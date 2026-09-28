using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MusicDraft.Core.Hardware;

public sealed record GpuAdapter(string Name, uint VendorId, long DedicatedVideoMemory, bool IsSoftware)
{
    public bool IsNvidia => VendorId == 0x10DE;
    public double DedicatedGb => DedicatedVideoMemory / 1024.0 / 1024 / 1024;
}

public sealed record HardwareInfo(
    IReadOnlyList<GpuAdapter> Adapters,
    GpuAdapter? PreferredGpu,
    string? NvidiaDriverVersion,
    double TotalRamGb,
    double AvailableRamGb)
{
    public string Describe() => PreferredGpu is { } g
        ? $"{g.Name}, {g.DedicatedGb:0.#} GB dedicated VRAM{(NvidiaDriverVersion != null ? $", driver {NvidiaDriverVersion}" : "")}; {TotalRamGb:0.#} GB RAM"
        : $"No discrete GPU detected; {TotalRamGb:0.#} GB RAM";
}

/// <summary>Reads OS-reported adapter memory via DXGI (WMI's AdapterRAM is capped at 4 GB and is not used).</summary>
public static class HardwareProbe
{
    public static HardwareInfo Probe()
    {
        var adapters = EnumerateDxgi();
        var preferred = adapters.Where(a => !a.IsSoftware && a.IsNvidia).OrderByDescending(a => a.DedicatedVideoMemory).FirstOrDefault()
                        ?? adapters.Where(a => !a.IsSoftware).OrderByDescending(a => a.DedicatedVideoMemory).FirstOrDefault();
        var (total, avail) = Memory();
        return new HardwareInfo(adapters, preferred, preferred?.IsNvidia == true ? NvidiaDriver() : null, total, avail);
    }

    public static long FreeDiskBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path))!;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return long.MaxValue; }
    }

    private static string? NvidiaDriver()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=driver_version --format=csv,noheader")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var s = p.StandardOutput.ReadToEnd().Trim().Split('\n')[0].Trim();
            p.WaitForExit(3000);
            return s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    // ---------- memory ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

    private static (double, double) Memory()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref m) ? (m.TotalPhys / 1073741824.0, m.AvailPhys / 1073741824.0) : (0, 0);
    }

    // ---------- DXGI ----------

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private static unsafe List<GpuAdapter> EnumerateDxgi()
    {
        var list = new List<GpuAdapter>();
        var iid = IidFactory1;
        if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == IntPtr.Zero) return list;
        try
        {
            // IDXGIFactory1 vtable: IUnknown(3) + IDXGIObject(4) + IDXGIFactory(5) => EnumAdapters1 at slot 12.
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)factory)[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    // IDXGIAdapter1 vtable: IUnknown(3) + IDXGIObject(4) + IDXGIAdapter(3) => GetDesc1 at slot 10.
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DxgiAdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
                    DxgiAdapterDesc1 d;
                    if (getDesc1(adapter, &d) >= 0)
                    {
                        var name = new string(d.Description).TrimEnd('\0');
                        list.Add(new GpuAdapter(name, d.VendorId, (long)d.DedicatedVideoMemory, (d.Flags & 2) != 0));
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        catch { /* DXGI unavailable: report no adapters */ }
        finally { Marshal.Release(factory); }
        return list;
    }
}
