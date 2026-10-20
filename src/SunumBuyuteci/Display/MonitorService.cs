using System.Runtime.InteropServices;
using SunumBuyuteci.Native;

namespace SunumBuyuteci.Display;

public static class MonitorService
{
    /// <summary>Returns all monitors: primary first, the rest sorted left to right.</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            {
                list.Add(new MonitorInfo
                {
                    DeviceName = mi.szDevice,
                    Bounds = mi.rcMonitor,
                    WorkArea = mi.rcWork,
                    IsPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                });
            }
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        var sorted = list.OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();

        var names = GetFriendlyNames();
        for (int i = 0; i < sorted.Count; i++)
        {
            var m = sorted[i];
            m.Number = i + 1;
            m.FriendlyName = names.TryGetValue(m.DeviceName, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n : $"Ekran {m.Number}";
        }
        return sorted;
    }

    /// <summary>Maps GDI device name (\\.\DISPLAY1) to the monitor's friendly name.</summary>
    private static Dictionary<string, string> GetFriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (DisplayConfig.GetDisplayConfigBufferSizes(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, out uint pc, out uint mc) != 0)
                return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[pc];
            var modes = new DISPLAYCONFIG_MODE_INFO[mc];
            if (DisplayConfig.QueryDisplayConfig(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero) != 0)
                return result;

            for (int i = 0; i < pc; i++)
            {
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header.type = DisplayConfig.GET_SOURCE_NAME;
                src.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                src.header.adapterId = paths[i].sourceInfo.adapterId;
                src.header.id = paths[i].sourceInfo.id;
                if (DisplayConfig.DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgt.header.type = DisplayConfig.GET_TARGET_NAME;
                tgt.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                tgt.header.adapterId = paths[i].targetInfo.adapterId;
                tgt.header.id = paths[i].targetInfo.id;
                if (DisplayConfig.DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;

                result[src.viewGdiDeviceName] = tgt.monitorFriendlyDeviceName;
            }
        }
        catch (Exception)
        {
            // Friendly names are cosmetic; fall back to "Ekran N".
        }
        return result;
    }
}
