using System.Runtime.InteropServices;

namespace OpenDesk;

internal sealed class TouchInput : IDisposable
{
    private const uint Touch = 2;
    private const uint InContact = 0x06;
    private const uint Down = 0x10000;
    private const uint Update = 0x20000;
    private const uint Up = 0x40000;
    private const uint Canceled = 0x8000;
    internal const int MaxContacts = 5;
    private readonly object sync = new();
    private readonly Dictionary<int, Contact> contacts = new(MaxContacts);
    private readonly PointerTypeInfo[] frame = new PointerTypeInfo[MaxContacts];
    private nint device;
    private bool closed;
    private long lastEvent;

    internal bool Send(char phase, int id, Point point)
    {
        lock (sync)
        {
            if (closed) return false;
            if (phase == 'd')
            {
                if (contacts.Count >= MaxContacts || contacts.ContainsKey(id)) return false;
                if (device == 0) device = CreateSyntheticPointerDevice(Touch, MaxContacts, 3);
                if (device == 0) return false;
                uint slot = 1;
                while (contacts.Values.Any(contact => contact.Slot == slot)) slot++;
                contacts.Add(id, new Contact(slot, point));
                return Inject(id, Down | InContact);
            }
            if (!contacts.TryGetValue(id, out var previous)) return false;
            if (phase == 'c')
                return EndContact(id, canceled: true);
            if (point != previous.Position)
            {
                contacts[id] = previous with { Position = point };
                if (!Inject(id, Update | InContact)) return false;
            }
            else if (phase == 'm' && !Inject(id, Update | InContact)) return false;
            return phase != 'u' || EndContact(id, canceled: false);
        }
    }

    private bool Inject(int id, uint flags, bool endAll = false)
    {
        Rectangle virtualDesktop = SystemInformation.VirtualScreen;
        int count = 0;
        foreach (var (pointer, contact) in contacts)
        {
            uint state = endAll || pointer == id ? flags : Update | InContact;
            var pixels = new Point(contact.Position.X - virtualDesktop.X, contact.Position.Y - virtualDesktop.Y);
            frame[count++] = new PointerTypeInfo
            {
                Type = Touch,
                Touch = new TouchInfo
                {
                    Pointer = new PointerInfo
                    {
                        Type = Touch, Id = contact.Slot, Flags = state, Pixels = pixels,
                        ButtonChange = (state & Down) != 0 ? 1u : (state & Up) != 0 ? 2u : 0u
                    },
                    Mask = 1,
                    Contact = new ContactRect(pixels.X - 2, pixels.Y - 2, pixels.X + 2, pixels.Y + 2)
                }
            };
        }
        if (!InjectSyntheticPointerInput(device, frame, (uint)count))
        {
            CloseDevice();
            return false;
        }
        lastEvent = Environment.TickCount64;
        return true;
    }

    internal void CancelIfIdle()
    {
        lock (sync)
        {
            if (contacts.Count > 0 && Environment.TickCount64 - lastEvent > 4500) CancelContacts();
        }
    }

    internal void Cancel()
    {
        lock (sync) CancelContacts();
    }

    private bool EndContact(int id, bool canceled)
    {
        bool result = Inject(id, canceled ? Up | Canceled : Up);
        contacts.Remove(id);
        return result;
    }

    private void CancelContacts()
    {
        if (contacts.Count == 0) return;
        Inject(0, Up | Canceled, endAll: true);
        contacts.Clear();
    }

    private void CloseDevice()
    {
        if (device != 0) DestroySyntheticPointerDevice(device);
        device = 0;
        contacts.Clear();
    }

    public void Dispose()
    {
        lock (sync)
        {
            closed = true;
            CancelContacts();
            CloseDevice();
        }
    }

    private readonly record struct Contact(uint Slot, Point Position);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerTypeInfo
    {
        internal uint Type;
        internal TouchInfo Touch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TouchInfo
    {
        internal PointerInfo Pointer;
        internal uint Flags, Mask;
        internal ContactRect Contact, ContactRaw;
        internal uint Orientation, Pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ContactRect(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        internal uint Type, Id, Frame, Flags;
        internal nint Device, Window;
        internal Point Pixels, Himetric, RawPixels, RawHimetric;
        internal uint Time, HistoryCount;
        internal int InputData;
        internal uint KeyStates;
        internal ulong PerformanceCount;
        internal uint ButtonChange;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint feedbackMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectSyntheticPointerInput(nint device, [In] PointerTypeInfo[] frame, uint count);

    [DllImport("user32.dll")]
    private static extern void DestroySyntheticPointerDevice(nint device);
}
