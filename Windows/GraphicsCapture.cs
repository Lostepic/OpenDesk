using System.Runtime.InteropServices;
using PropertyType = Windows.Foundation.PropertyType;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace OpenDesk;

// One frame pool per shared monitor. Read back only the newest frame available.
internal sealed class GraphicsCapture : IDisposable
{
    private readonly IDirect3DDevice device;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;
    private readonly StreamProfile profile;
    private readonly bool reportsDirtyRegions;
    private readonly byte[] previousPixels;
    private int frameRate;
    private volatile bool closed;

    internal GraphicsCapture(DisplayChoice display, StreamProfile profile)
    {
        this.profile = profile;
        Rectangle bounds = display.Bounds;
        previousPixels = GC.AllocateUninitializedArray<byte>(checked(bounds.Width * bounds.Height * 4));
        nint monitor = MonitorFromPoint(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), 0);
        if (monitor == 0 || !GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows Graphics Capture is unavailable for this display.");

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) || info.DeviceName != display.DeviceName)
            throw new InvalidOperationException("The capture monitor changed.");

        item = CreateItem(monitor);
        if (item.Size.Width != bounds.Width || item.Size.Height != bounds.Height)
            throw new InvalidOperationException("The capture monitor dimensions changed.");

        reportsDirtyRegions = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);
        device = CreateDevice();
        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            try
            {
                session = pool.CreateCaptureSession(item);
                try
                {
                    if (reportsDirtyRegions)
                    {
                        session.DirtyRegionMode = GraphicsCaptureDirtyRegionMode.ReportOnly;
                        SetFrameRate(profile.Fps);
                    }
                    item.Closed += OnItemClosed;
                    try { session.StartCapture(); }
                    catch
                    {
                        item.Closed -= OnItemClosed;
                        throw;
                    }
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }
            catch
            {
                pool.Dispose();
                throw;
            }
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    internal async Task<byte[]?> GrabAsync(bool requireFrame)
    {
        if (closed)
            throw new InvalidOperationException("Windows closed the capture source.");

        using var first = pool.TryGetNextFrame();
        if (first is null)
            return null;
        using var second = pool.TryGetNextFrame();
        var frame = second ?? first;
        if (!requireFrame && reportsDirtyRegions && first.DirtyRegions.Count == 0 &&
            (second is null || second.DirtyRegions.Count == 0))
            return null;

        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);
        if (!PixelsChanged(bitmap, requireFrame))
            return null;

        using var stream = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(profile.Quality / 100f, PropertyType.Single)
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetSoftwareBitmap(bitmap);
        double scale = Math.Min(1, Math.Min((double)profile.MaxWidth / bitmap.PixelWidth,
            (double)profile.MaxHeight / bitmap.PixelHeight));
        if (scale < 1)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, (int)(bitmap.PixelWidth * scale));
            encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, (int)(bitmap.PixelHeight * scale));
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
        }
        await encoder.FlushAsync();
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint size = checked((uint)stream.Size);
        if (await reader.LoadAsync(size) != size)
            throw new InvalidOperationException("The captured JPEG could not be read.");
        byte[] bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    internal void SetFrameRate(int fps)
    {
        if (!reportsDirtyRegions || fps == frameRate)
            return;
        session.MinUpdateInterval = TimeSpan.FromSeconds(1.0 / fps);
        frameRate = fps;
    }

    private unsafe bool PixelsChanged(SoftwareBitmap bitmap, bool first)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* pixels, out uint length);
        int rowBytes = checked(bitmap.PixelWidth * 4);
        int height = bitmap.PixelHeight;
        if (plane.StartIndex < 0 || plane.Stride < rowBytes || pixels == null ||
            (long)plane.StartIndex + (long)plane.Stride * (height - 1) + rowBytes > length ||
            (long)rowBytes * height != previousPixels.Length)
            throw new InvalidOperationException("The capture frame layout changed.");

        bool changed = first;
        for (int row = 0; row < height; row++)
        {
            var source = new ReadOnlySpan<byte>(pixels + plane.StartIndex + row * plane.Stride, rowBytes);
            var saved = previousPixels.AsSpan(row * rowBytes, rowBytes);
            if (first || !source.SequenceEqual(saved))
            {
                source.CopyTo(saved);
                changed = true;
            }
        }
        return changed;
    }

    public void Dispose()
    {
        item.Closed -= OnItemClosed;
        session.Dispose();
        pool.Dispose();
        device.Dispose();
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => closed = true;

    private static GraphicsCaptureItem CreateItem(nint monitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        nint item = interop.CreateForMonitor(monitor, new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"));
        try { return GraphicsCaptureItem.FromAbi(item); }
        finally { Marshal.Release(item); }
    }

    private static IDirect3DDevice CreateDevice()
    {
        const uint bgraSupport = 0x20;
        const uint sdkVersion = 7;
        const uint hardware = 1;
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, hardware, 0, bgraSupport, 0, 0,
            sdkVersion, out nint nativeDevice, 0, 0));
        try
        {
            Guid dxgiId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeDevice, in dxgiId, out nint dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint inspectable));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgiDevice); }
        }
        finally { Marshal.Release(nativeDevice); }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal int Left, Top, Right, Bottom;
        internal int WorkLeft, WorkTop, WorkRight, WorkBottom;
        internal int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software,
        uint flags, nint levels, uint levelCount, uint sdkVersion, out nint device, nint level, nint context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint device);
}
