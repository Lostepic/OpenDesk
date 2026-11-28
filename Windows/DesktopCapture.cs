using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OpenDesk;

internal sealed record DisplayChoice(string DeviceName, string Identity, string Name, Rectangle Bounds, bool IsVirtual)
{
    public override string ToString() => $"{Name}  ·  {Bounds.Width} × {Bounds.Height}";

    internal static DisplayChoice[] GetActive() => Screen.AllScreens.Select(screen =>
    {
        var monitor = new Native.DisplayDevice { Size = Marshal.SizeOf<Native.DisplayDevice>() };
        var adapter = new Native.DisplayDevice { Size = monitor.Size };
        Native.EnumDisplayDevices(screen.DeviceName, 0, ref monitor, 1);
        for (uint index = 0; Native.EnumDisplayDevices(null, index, ref adapter, 0); index++)
        {
            if (adapter.Name == screen.DeviceName)
                break;
            adapter = new Native.DisplayDevice { Size = monitor.Size };
        }

        string number = screen.DeviceName[(screen.DeviceName.LastIndexOf('\\') + 1)..];
        string label = string.IsNullOrWhiteSpace(monitor.Label) ? adapter.Label : monitor.Label;
        bool isVirtual = monitor.Id?.Contains("MTT1337", StringComparison.OrdinalIgnoreCase) == true ||
            adapter.Label?.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase) == true;
        if (isVirtual)
            label = "Virtual display";
        string name = string.IsNullOrWhiteSpace(label) ? number : $"{number} · {label}";
        if (screen.Primary)
            name += " (primary)";
        return new DisplayChoice(screen.DeviceName, $"{adapter.Id}|{monitor.Id}", name, screen.Bounds, isVirtual);
    }).OrderBy(display => display.DeviceName, StringComparer.Ordinal).ToArray();
}

internal sealed record StreamProfile(string Name, int MaxWidth, int MaxHeight, int Fps, long Quality)
{
    public override string ToString() => Name;

    internal static readonly StreamProfile[] All =
    [
        new("Low bandwidth · up to 960 × 600 · 24 fps", 960, 600, 24, 67),
        new("Balanced · up to 1600 × 1000 · 45 fps", 1600, 1000, 45, 82),
        new("Sharp · up to 1920 × 1200 · 60 fps", 1920, 1200, 60, 89)
    ];
}

internal sealed class DesktopCapture : IDisposable
{
    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders()
        .Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

    private readonly Bitmap source;
    private readonly Bitmap? scaled;
    private readonly EncoderParameters encoder = new(1);
    private readonly MemoryStream buffer = new();
    private readonly Rectangle bounds;
    private readonly byte[] previousPixels;
    private byte[]? encoded;

    internal DesktopCapture(Rectangle bounds, StreamProfile profile)
    {
        this.bounds = bounds;
        source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        previousPixels = GC.AllocateUninitializedArray<byte>(checked(bounds.Width * bounds.Height * 4));
        double scale = Math.Min(1, Math.Min(
            (double)profile.MaxWidth / bounds.Width, (double)profile.MaxHeight / bounds.Height));
        if (scale < 1)
            scaled = new Bitmap(
                Math.Max(1, (int)(bounds.Width * scale)),
                Math.Max(1, (int)(bounds.Height * scale)), PixelFormat.Format32bppRgb);
        encoder.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, profile.Quality);
    }

    internal byte[] Grab()
    {
        using (Graphics graphics = Graphics.FromImage(source))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                CopyPixelOperation.SourceCopy);
            DrawCursor(graphics);
        }

        if (!PixelsChanged() && encoded is not null)
            return encoded;

        if (scaled is not null)
        {
            using var graphics = Graphics.FromImage(scaled);
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        }

        buffer.SetLength(0);
        (scaled ?? source).Save(buffer, Jpeg, encoder);
        return encoded = buffer.ToArray();
    }

    private unsafe bool PixelsChanged()
    {
        var data = source.LockBits(new Rectangle(Point.Empty, bounds.Size), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = bounds.Width * 4;
            if (data.Stride == rowBytes)
            {
                var pixels = new ReadOnlySpan<byte>((void*)data.Scan0, previousPixels.Length);
                if (encoded is not null && pixels.SequenceEqual(previousPixels))
                    return false;
                pixels.CopyTo(previousPixels);
                return true;
            }
            bool changed = encoded is null;
            for (int row = 0; row < bounds.Height; row++)
            {
                var pixels = new ReadOnlySpan<byte>((void*)(data.Scan0 + row * data.Stride), rowBytes);
                var previous = previousPixels.AsSpan(row * rowBytes, rowBytes);
                if (!changed && !pixels.SequenceEqual(previous))
                    changed = true;
                if (changed)
                    pixels.CopyTo(previous);
            }
            return changed;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }
    private void DrawCursor(Graphics graphics)
    {
        var cursor = new Native.CursorInfo { Size = Marshal.SizeOf<Native.CursorInfo>() };
        if (!Native.GetCursorInfo(ref cursor) || cursor.Flags != 1 ||
            !bounds.Contains(cursor.X, cursor.Y) || !Native.GetIconInfo(cursor.Handle, out var icon))
            return;

        try
        {
            nint dc = graphics.GetHdc();
            try
            {
                Native.DrawIconEx(dc, cursor.X - bounds.X - icon.HotspotX,
                    cursor.Y - bounds.Y - icon.HotspotY, cursor.Handle, 0, 0, 0, 0, 3);
            }
            finally
            {
                graphics.ReleaseHdc(dc);
            }
        }
        finally
        {
            if (icon.Mask != 0)
                Native.DeleteObject(icon.Mask);
            if (icon.Color != 0)
                Native.DeleteObject(icon.Color);
        }
    }

    public void Dispose()
    {
        source.Dispose();
        scaled?.Dispose();
        encoder.Dispose();
        buffer.Dispose();
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal int Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }

    internal static bool IsMovingWindowOutside(Rectangle display)
    {
        const int inMoveSize = 0x2;
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref gui) || (gui.Flags & inMoveSize) == 0)
            return false;
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref cursor) && !display.Contains(cursor.X, cursor.Y);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        internal int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Label;
        internal int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CursorInfo
    {
        internal int Size, Flags;
        internal nint Handle;
        internal int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        internal int IsIcon, HotspotX, HotspotY;
        internal nint Mask, Color;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice data, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorInfo(ref CursorInfo cursor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, int step,
        nint brush, int flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint obj);
}
