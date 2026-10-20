using System.Runtime.InteropServices;

namespace SunumBuyuteci.Native;

/// <summary>3x3 transform matrix (row-major) used by the magnifier control.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MAGTRANSFORM
{
    public float m00, m01, m02, m10, m11, m12, m20, m21, m22;

    public static MAGTRANSFORM Scale(float scale) => new() { m00 = scale, m11 = scale, m22 = 1f };
}

public static class Magnification
{
    public const string WC_MAGNIFIER = "Magnifier";
    public const uint MS_SHOWMAGNIFIEDCURSOR = 0x0001;
    public const uint MW_FILTERMODE_EXCLUDE = 0;

    [DllImport("Magnification.dll")]
    public static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    public static extern bool MagUninitialize();

    [DllImport("Magnification.dll")]
    public static extern bool MagSetWindowSource(IntPtr hwnd, RECT rect);

    [DllImport("Magnification.dll")]
    public static extern bool MagSetWindowTransform(IntPtr hwnd, ref MAGTRANSFORM transform);

    [DllImport("Magnification.dll")]
    public static extern bool MagSetWindowFilterList(IntPtr hwnd, uint filterMode, int count, IntPtr[] windows);
}
