using SunumBuyuteci.Native;

namespace SunumBuyuteci.Display;

public sealed class MonitorInfo
{
    /// <summary>1 = primary, others ordered left to right.</summary>
    public int Number { get; set; }
    public string DeviceName { get; init; } = "";
    public string FriendlyName { get; set; } = "";
    public RECT Bounds { get; init; }
    public RECT WorkArea { get; init; }
    public bool IsPrimary { get; init; }

    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
    public string Resolution => $"{Width}×{Height}";
}
