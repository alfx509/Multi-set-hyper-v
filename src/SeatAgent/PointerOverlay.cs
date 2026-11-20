using System.Runtime.InteropServices;

namespace MultiSeat.Agent;

/// <summary>
/// Draws the seat's mouse pointer itself. In a Remote Desktop seat the viewer window draws the pointer at the position
/// of the viewer's own mouse – but the main desktop's mouse is kept away from players' monitors, so players saw no
/// pointer. This shows the current pointer shape in a small see-through, click-through, always-on-top window at the
/// pointer position, and hides it when a program hides the pointer (games). Runs on its own thread.
/// </summary>
internal sealed class PointerOverlay : IDisposable
{
    private const int Size = 96; // room for large / scaled pointers

    private readonly Thread _thread;
    private readonly Action<string> _log;
    private volatile bool _stop;

    private PointerOverlay(Action<string> log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Pointer overlay" };
        _thread.Start();
    }

    public static PointerOverlay Start(Action<string> log) => new(log);

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
    }

    private void Run()
    {
        // Physical pixels everywhere (the pointer position and the window position must use the same units).
        SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

        // The window must be on the desktop the player sees.
        var desktop = OpenInputDesktop(0, false, 0x10000000);
        if (desktop != IntPtr.Zero)
        {
            SetThreadDesktop(desktop);
        }

        WndProc proc = DefWindowProcW;
        var cls = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(proc),
            Instance = GetModuleHandleW(null),
            ClassName = "MultiSeatPointer",
        };
        RegisterClassExW(ref cls);
        var hwnd = CreateWindowExW(WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate,
            "MultiSeatPointer", "", WsPopup, 0, 0, Size, Size, IntPtr.Zero, IntPtr.Zero, cls.Instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            _log($"Pointer: could not create the pointer window ({Marshal.GetLastWin32Error()}).");
            return;
        }

        var screen = GetDC(IntPtr.Zero);
        var black = new Canvas(screen);
        var white = new Canvas(screen);
        var output = new Canvas(screen);
        IntPtr shape = IntPtr.Zero;
        Point hotspot = default;
        Point placed = new(int.MinValue, int.MinValue);
        var visible = false;
        var lastTop = 0L;

        try
        {
            while (!_stop)
            {
                while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    DispatchMessageW(ref msg);
                }

                var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
                var show = GetCursorInfo(ref info) && (info.Flags & 1) != 0 && info.Cursor != IntPtr.Zero;
                if (!show)
                {
                    if (visible)
                    {
                        ShowWindow(hwnd, 0);
                        visible = false;
                    }

                    Thread.Sleep(16);
                    continue;
                }

                if (info.Cursor != shape)
                {
                    shape = info.Cursor;
                    hotspot = Render(shape, black, white, output);
                    placed = new Point(int.MinValue, int.MinValue);
                }

                var at = new Point(info.Position.X - hotspot.X, info.Position.Y - hotspot.Y);
                if (at.X != placed.X || at.Y != placed.Y || !visible)
                {
                    var size = new SizeStruct(Size, Size);
                    var src = default(Point);
                    var blend = new Blend { Op = 0, Flags = 0, SourceAlpha = 255, AlphaFormat = 1 };
                    UpdateLayeredWindow(hwnd, screen, ref at, ref size, output.Dc, ref src, 0, ref blend, 2);
                    placed = at;
                    if (!visible)
                    {
                        ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
                        visible = true;
                    }
                }

                var now = Environment.TickCount64;
                if (now - lastTop > 1000)
                {
                    lastTop = now; // stay above windows that became topmost later
                    SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
                }

                Thread.Sleep(4);
            }
        }
        finally
        {
            DestroyWindow(hwnd);
            black.Dispose();
            white.Dispose();
            output.Dispose();
            ReleaseDC(IntPtr.Zero, screen);
            GC.KeepAlive(proc);
        }
    }

    /// <summary>
    /// Draws the pointer on black and on white and derives per-pixel alpha from the difference, so color pointers
    /// and old black-and-white (mask) pointers both come out right. Returns the hotspot.
    /// </summary>
    private static Point Render(IntPtr cursor, Canvas black, Canvas white, Canvas output)
    {
        black.Fill(0x00000000);
        white.Fill(0x00FFFFFF);
        DrawIconEx(black.Dc, 0, 0, cursor, 0, 0, 0, IntPtr.Zero, 3);
        DrawIconEx(white.Dc, 0, 0, cursor, 0, 0, 0, IntPtr.Zero, 3);
        var b = black.Pixels;
        var w = white.Pixels;
        var o = output.Pixels;
        for (var i = 0; i < Size * Size; i++)
        {
            var pb = Marshal.ReadInt32(b, i * 4);
            var pw = Marshal.ReadInt32(w, i * 4);
            var diff = (((pw >> 8) & 0xFF) - ((pb >> 8) & 0xFF) + ((pw >> 16) & 0xFF) - ((pb >> 16) & 0xFF) + (pw & 0xFF) - (pb & 0xFF)) / 3;
            if (diff < -64)
            {
                // Inverting pixel (text cursor): darker on white than on black. A layered window cannot invert what
                // is below it, so draw it solid black – visible on the usual light text fields.
                Marshal.WriteInt32(o, i * 4, unchecked((int)0xFF000000));
                continue;
            }

            var alpha = Math.Clamp(255 - diff, 0, 255);
            // Drawn on black = color already multiplied by alpha (premultiplied, as UpdateLayeredWindow wants).
            var r = Math.Min((pb >> 16) & 0xFF, alpha);
            var g = Math.Min((pb >> 8) & 0xFF, alpha);
            var bl = Math.Min(pb & 0xFF, alpha);
            Marshal.WriteInt32(o, i * 4, (alpha << 24) | (r << 16) | (g << 8) | bl);
        }

        var hotspot = default(Point);
        if (GetIconInfo(cursor, out var icon))
        {
            hotspot = new Point(icon.XHotspot, icon.YHotspot);
            if (icon.Mask != IntPtr.Zero)
            {
                DeleteObject(icon.Mask);
            }

            if (icon.Color != IntPtr.Zero)
            {
                DeleteObject(icon.Color);
            }
        }

        return hotspot;
    }

    /// <summary>A 32-bit top-down bitmap of Size×Size selected into its own memory DC.</summary>
    private sealed class Canvas : IDisposable
    {
        private readonly IntPtr _bitmap;
        private readonly IntPtr _old;

        public Canvas(IntPtr screen)
        {
            Dc = CreateCompatibleDC(screen);
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = Size,
                Height = -Size,
                Planes = 1,
                BitCount = 32,
            };
            _bitmap = CreateDIBSection(Dc, ref header, 0, out var bits, IntPtr.Zero, 0);
            Pixels = bits;
            _old = SelectObject(Dc, _bitmap);
        }

        public IntPtr Dc { get; }

        public IntPtr Pixels { get; }

        public void Fill(int value)
        {
            for (var i = 0; i < Size * Size; i++)
            {
                Marshal.WriteInt32(Pixels, i * 4, value);
            }
        }

        public void Dispose()
        {
            SelectObject(Dc, _old);
            DeleteObject(_bitmap);
            DeleteDC(Dc);
        }
    }

    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeStruct
    {
        public int Cx;
        public int Cy;

        public SizeStruct(int cx, int cy)
        {
            Cx = cx;
            Cy = cy;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Point Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int XHotspot;
        public int YHotspot;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blend
    {
        public byte Op;
        public byte Flags;
        public byte SourceAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref Point at, ref SizeStruct size, IntPtr src,
        ref Point srcAt, int key, ref Blend blend, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
