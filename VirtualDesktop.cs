using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenDesk;

internal static class VirtualDesktop
{
    private const uint ActivePath = 1;
    private const uint InvalidMode = uint.MaxValue;
    private const uint Apply = 0x80;
    private const uint UseSuppliedConfig = 0x20;
    private const uint SaveToDatabase = 0x200;
    private const uint AllowChanges = 0x400;

    internal readonly record struct Target(Luid Adapter, uint Id);
    internal readonly record struct Lease(Target Monitor, DisplayChoice Display);

    internal static void ParkDisplays()
    {
        var (paths, modes) = ReadPaths(2);
        var remaining = paths.Where(path => !IsVirtual(path)).ToArray();
        if (remaining.Length != paths.Length && remaining.Length != 0)
            SetPaths(remaining, modes, SaveToDatabase);
    }

    internal static async Task<Lease> AttachAsync(Size size, Target[] assigned, Point? preferred, CancellationToken cancellation)
    {
        var (paths, modes) = ReadPaths(1);
        var existing = paths.Where(IsActive).ToArray();
        var pending = existing.FirstOrDefault(path => !assigned.Contains(path.Target.Key) && IsVirtual(path));
        if (!IsActive(pending))
            pending = paths.FirstOrDefault(path => !IsActive(path) && path.Target.Available != 0 &&
                !existing.Any(current => current.Target.Key == path.Target.Key ||
                    (current.Source.Adapter.Equals(path.Source.Adapter) && current.Source.Id == path.Source.Id)) &&
                IsVirtual(path));
        if (pending.Target.Available == 0)
            throw new InvalidOperationException("No unused virtual monitor is available.");

        Target target = pending.Target.Key;
        if (!IsActive(pending))
        {
            var displays = DisplayChoice.GetActive();
            Rectangle edge = displays.MaxBy(display => display.Bounds.Right)?.Bounds ?? Rectangle.Empty;
            Point position = preferred is { } saved &&
                !displays.Any(display => display.Bounds.IntersectsWith(new Rectangle(saved, size)))
                ? saved : new Point(edge.Right, edge.Top);
            pending.Flags |= ActivePath;
            pending.Source.ModeIndex = (uint)modes.Length;
            pending.Target.ModeIndex = InvalidMode;
            modes = [.. modes, new Mode
            {
                Kind = 1,
                Adapter = pending.Source.Adapter,
                Id = pending.Source.Id,
                Source = new SourceMode { Width = (uint)size.Width, Height = (uint)size.Height,
                    PixelFormat = 4, X = position.X, Y = position.Y }
            }];
            SetPaths([.. existing, pending], modes, 0);
        }
        try
        {
            bool modeRequested = false;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                var display = GetDisplay(target);
                if (display is not null)
                {
                    if (display.Bounds.Size == size || (!modeRequested && !TrySetResolution(display.DeviceName, size)))
                        return new Lease(target, display);
                    modeRequested = true;
                    if (attempt >= 32)
                        return new Lease(target, display);
                }
                await Task.Delay(125, cancellation);
            }
            throw new InvalidOperationException("Windows did not attach the virtual monitor.");
        }
        catch
        {
            Detach(target);
            throw;
        }
    }

    internal static void RestorePositions(IReadOnlyDictionary<Target, Point> positions)
    {
        if (positions.Count == 0) return;
        var (paths, modes) = ReadPaths(2);
        var occupied = new List<Rectangle>();
        bool changed = false;
        foreach (var path in paths)
        {
            if (path.Source.ModeIndex >= modes.Length) continue;
            ref var source = ref modes[path.Source.ModeIndex].Source;
            if (positions.TryGetValue(path.Target.Key, out Point saved) &&
                (source.X != saved.X || source.Y != saved.Y))
            {
                source.X = saved.X;
                source.Y = saved.Y;
                changed = true;
            }
            var bounds = new Rectangle(source.X, source.Y, (int)source.Width, (int)source.Height);
            if (occupied.Any(existing => existing != bounds && existing.IntersectsWith(bounds)))
                return;
            occupied.Add(bounds);
        }
        if (changed)
            SetPaths(paths, modes, 0);
    }

    internal static DisplayChoice? GetDisplay(Target target)
    {
        var active = ReadPaths(2).Paths.FirstOrDefault(path => path.Target.Key == target);
        if (!IsActive(active)) return null;
        string? name = GetSourceName(active.Source);
        return DisplayChoice.GetActive().FirstOrDefault(item => item.DeviceName == name);
    }

    private static bool TrySetResolution(string display, Size size)
    {
        // DEVMODEW has a 220-byte fixed layout. Supply only the pixel dimensions so Windows
        // keeps that display's current position, colour depth, refresh rate and rotation.
        const int settingsSize = 220;
        const int dimensions = 0x180000;
        var settings = new byte[settingsSize];
        for (int index = 0; ; index++)
        {
            BitConverter.GetBytes((ushort)settingsSize).CopyTo(settings, 68);
            if (!EnumDisplaySettings(display, index, settings)) return false;
            if (BitConverter.ToInt32(settings, 172) != size.Width ||
                BitConverter.ToInt32(settings, 176) != size.Height)
                continue;
            BitConverter.GetBytes(dimensions).CopyTo(settings, 72);
            return ChangeDisplaySettingsEx(display, settings, 0, 0, 0) == 0;
        }
    }

    internal static void Detach(Target monitor)
    {
        var (paths, modes) = ReadPaths(2);
        var remaining = paths.Where(path => path.Target.Key != monitor).ToArray();
        if (remaining.Length != paths.Length && remaining.Length != 0)
            SetPaths(remaining, modes, 0);
    }

    private static bool IsActive(Path path) => (path.Flags & ActivePath) != 0;

    private static bool IsVirtual(Path path)
    {
        var name = new TargetName
        {
            Header = new DeviceHeader(2, (uint)Marshal.SizeOf<TargetName>(), path.Target.Adapter, path.Target.Id)
        };
        return GetTargetInfo(ref name) == 0 &&
            (name.MonitorPath?.Contains("DISPLAY#MTT1337", StringComparison.OrdinalIgnoreCase) == true ||
                string.Equals(name.FriendlyName, "VDD by MTT", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetSourceName(PathSource source)
    {
        var name = new SourceName
        {
            Header = new DeviceHeader(1, (uint)Marshal.SizeOf<SourceName>(), source.Adapter, source.Id)
        };
        return GetSourceInfo(ref name) == 0 ? name.GdiName : null;
    }

    private static (Path[] Paths, Mode[] Modes) ReadPaths(uint flags)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount));
            var paths = new Path[pathCount];
            var modes = new Mode[modeCount];
            int result = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, 0);
            if (result == 122) continue;
            Check(result);
            return (paths[..(int)pathCount], modes[..(int)modeCount]);
        }
        throw new InvalidOperationException("The Windows display configuration is changing.");
    }

    private static void SetPaths(Path[] paths, Mode[] modes, uint extraFlags) =>
        Check(SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            Apply | UseSuppliedConfig | AllowChanges | extraFlags));

    private static void Check(int error)
    {
        if (error != 0) throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Luid(uint Low, int High);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DeviceHeader(uint Type, uint Size, Luid Adapter, uint Id);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        internal DeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string GdiName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        internal DeviceHeader Header;
        internal uint Flags, Technology;
        internal ushort Manufacturer, Product;
        internal uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string MonitorPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Path
    {
        internal PathSource Source;
        internal PathTarget Target;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSource
    {
        internal Luid Adapter;
        internal uint Id, ModeIndex, StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTarget
    {
        internal Luid Adapter;
        internal uint Id, ModeIndex, OutputTechnology, Rotation, Scaling, RefreshNumerator,
            RefreshDenominator, ScanLineOrdering, Available, StatusFlags;
        internal readonly Target Key => new(Adapter, Id);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceMode
    {
        internal uint Width, Height, PixelFormat;
        internal int X, Y;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct Mode
    {
        [FieldOffset(0)] internal uint Kind;
        [FieldOffset(4)] internal uint Id;
        [FieldOffset(8)] internal Luid Adapter;
        [FieldOffset(16)] internal SourceMode Source;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] Path[] paths,
        ref uint modeCount, [Out] Mode[] modes, nint topology);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, Path[] paths, uint modeCount, Mode[] modes, uint flags);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceInfo(ref SourceName source);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetInfo(ref TargetName target);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string display, int index, [In, Out] byte[] settings);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string display, byte[] settings, nint window, uint flags, nint arg);
}
