using System.Drawing.Drawing2D;
using MultiSeat.Core;

namespace MultiSeat.UI;

internal enum ChipKind
{
    Monitor,
    Keyboard,
    Mouse,
    Headset,
}

internal enum PlaceState
{
    Stopped,
    Starting,
    Playing,
    Problem,
}

/// <summary>What is being dragged: a monitor (by device name) or a keyboard/mouse, and the seat it comes from (null = spares).</summary>
internal sealed record DragItem(ChipKind Kind, string? MonitorId, InputDevice? Device, Seat? From);

/// <summary>A connected monitor: Windows device name, number on the desk (left to right), description.</summary>
internal sealed record MonitorInfo(string Id, int Number, string Text);

/// <summary>
/// The "Workplaces" view: one card per player (place) with its monitor, keyboard, mouse and headset, and a "Spares"
/// column with everything not given to a place. Drag a monitor, keyboard or mouse onto a place to give it to that
/// player, or back to Spares to take it away. Click a place to select it, double-click to edit it.
/// </summary>
internal sealed class WorkplacesView : Panel
{
    private readonly FlowLayoutPanel _flow = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        WrapContents = true,
        Padding = new Padding(8),
        BackColor = Color.FromArgb(236, 239, 244),
    };

    private readonly Dictionary<Seat, PlaceCard> _cards = new();
    private SparesColumn? _spares;
    private Seat? _selected;

    public WorkplacesView()
    {
        Controls.Add(_flow);
    }

    public event Action<Seat>? SeatSelected;

    public event Action<Seat>? SeatActivated;

    public event Action? AddRequested;

    /// <summary>A drag ended on a place (target) or on Spares (target null).</summary>
    public event Action<DragItem, Seat?>? Dropped;

    /// <summary>Right-click menu of a place (the main window's seat menu).</summary>
    public ContextMenuStrip? CardMenu { get; set; }

    /// <summary>While players are playing nothing can be dragged.</summary>
    public bool Locked { get; set; }

    public void SetData(IReadOnlyList<Seat> seats, IReadOnlyList<InputDevice> spareDevices, IReadOnlyList<MonitorInfo> monitors,
        bool localMode, bool canAdd)
    {
        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls)
        {
            c.Dispose();
        }

        _flow.Controls.Clear();
        _cards.Clear();

        var usedMonitors = seats.Select(s => s.Monitor).Where(m => m is not null).ToHashSet();
        _spares = new SparesColumn(this, monitors.Where(m => !usedMonitors.Contains(m.Id)).ToList(), spareDevices);
        _flow.Controls.Add(_spares);

        for (var i = 0; i < seats.Count; i++)
        {
            var card = new PlaceCard(this, seats[i], i + 1, monitors.FirstOrDefault(m => m.Id == seats[i].Monitor), localMode)
            {
                ContextMenuStrip = CardMenu,
            };
            _cards[seats[i]] = card;
            _flow.Controls.Add(card);
        }

        if (canAdd)
        {
            _flow.Controls.Add(new AddCard(this));
        }

        _flow.ResumeLayout();
        SelectSeat(_selected is not null && _cards.ContainsKey(_selected) ? _selected : null);
    }

    public void SetStatus(Seat seat, string text, PlaceState state)
    {
        if (_cards.TryGetValue(seat, out var card))
        {
            card.SetStatus(text, state);
        }
    }

    public void SelectSeat(Seat? seat)
    {
        _selected = seat;
        foreach (var (s, card) in _cards)
        {
            card.Selected = ReferenceEquals(s, seat);
        }
    }

    internal void PlaceClicked(Seat seat)
    {
        SelectSeat(seat);
        SeatSelected?.Invoke(seat);
    }

    internal void PlaceDoubleClicked(Seat seat) => SeatActivated?.Invoke(seat);

    internal void RequestAdd() => AddRequested?.Invoke();

    internal void Drop(DragItem item, Seat? target) => Dropped?.Invoke(item, target);

    // ------------------------------------------------------------------ drawing helpers

    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static Color Background(PlaceState state, bool console) => state switch
    {
        PlaceState.Playing => Color.FromArgb(205, 238, 208),
        PlaceState.Starting => Color.FromArgb(255, 226, 186),
        PlaceState.Problem => Color.FromArgb(252, 214, 214),
        _ => console ? Color.FromArgb(218, 230, 245) : Color.FromArgb(255, 246, 208),
    };

    internal static Color Dot(PlaceState state) => state switch
    {
        PlaceState.Playing => Color.FromArgb(30, 150, 60),
        PlaceState.Starting => Color.FromArgb(230, 140, 20),
        PlaceState.Problem => Color.FromArgb(200, 40, 40),
        _ => Color.FromArgb(120, 120, 120),
    };
}

/// <summary>Simple line drawings of the devices (our own art).</summary>
internal static class DeviceArt
{
    public static void Draw(Graphics g, ChipKind kind, RectangleF r, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, Math.Max(1.4f, r.Height / 14));
        using var fill = new SolidBrush(Color.FromArgb(40, color));
        switch (kind)
        {
            case ChipKind.Monitor:
            {
                var screen = new RectangleF(r.X, r.Y, r.Width, r.Height * 0.72f);
                using (var path = WorkplacesView.Rounded(screen, r.Height / 12))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }

                var cx = r.X + (r.Width / 2);
                g.DrawLine(pen, cx, screen.Bottom, cx, r.Bottom - (r.Height * 0.06f));
                g.DrawLine(pen, cx - (r.Width * 0.22f), r.Bottom - (r.Height * 0.06f), cx + (r.Width * 0.22f), r.Bottom - (r.Height * 0.06f));
                break;
            }

            case ChipKind.Keyboard:
            {
                var body = new RectangleF(r.X, r.Y + (r.Height * 0.2f), r.Width, r.Height * 0.6f);
                using (var path = WorkplacesView.Rounded(body, r.Height / 10))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }

                using var keys = new SolidBrush(color);
                var kw = body.Width / 11;
                var kh = body.Height / 5;
                for (var row = 0; row < 2; row++)
                {
                    for (var col = 0; col < 8; col++)
                    {
                        g.FillRectangle(keys, body.X + kw * (1.2f + col * 1.1f), body.Y + kh * (0.9f + row * 1.4f), kw * 0.7f, kh * 0.8f);
                    }
                }

                g.FillRectangle(keys, body.X + kw * 3, body.Y + kh * 3.7f, kw * 5, kh * 0.7f);
                break;
            }

            case ChipKind.Mouse:
            {
                var w = Math.Min(r.Width, r.Height * 0.62f);
                var body = new RectangleF(r.X + ((r.Width - w) / 2), r.Y + (r.Height * 0.05f), w, r.Height * 0.9f);
                g.FillEllipse(fill, body);
                g.DrawEllipse(pen, body);
                var cx = body.X + (body.Width / 2);
                g.DrawLine(pen, cx, body.Y, cx, body.Y + (body.Height * 0.38f));
                g.DrawLine(pen, body.X + (body.Width * 0.08f), body.Y + (body.Height * 0.38f), body.Right - (body.Width * 0.08f), body.Y + (body.Height * 0.38f));
                break;
            }

            case ChipKind.Headset:
            {
                var s = Math.Min(r.Width, r.Height);
                var box = new RectangleF(r.X + ((r.Width - s) / 2), r.Y + (r.Height * 0.08f), s, s * 0.95f);
                g.DrawArc(pen, box.X + (s * 0.12f), box.Y, s * 0.76f, s * 0.9f, 180, 180);
                using var ear = new SolidBrush(color);
                g.FillRectangle(ear, box.X + (s * 0.06f), box.Y + (s * 0.42f), s * 0.2f, s * 0.42f);
                g.FillRectangle(ear, box.Right - (s * 0.26f), box.Y + (s * 0.42f), s * 0.2f, s * 0.42f);
                break;
            }
        }
    }
}

/// <summary>A small device tile (icon + name). Can be dragged when it carries a <see cref="DragItem"/>.</summary>
internal sealed class DeviceChip : Control
{
    private readonly ChipKind _kind;
    private readonly bool _missing;
    private Point _down;
    private bool _pressed;

    public DeviceChip(ChipKind kind, string text, DragItem? item, bool missing = false, string? tip = null)
    {
        _kind = kind;
        _missing = missing;
        Item = item;
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(LogicalToDeviceUnits(150), LogicalToDeviceUnits(30));
        Margin = new Padding(LogicalToDeviceUnits(3));
        Cursor = item is null ? Cursors.Default : Cursors.Hand;
        if (tip is not null)
        {
            new ToolTip().SetToolTip(this, tip);
        }
    }

    public DragItem? Item { get; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = WorkplacesView.Rounded(bounds, Height / 5f))
        {
            using var back = new SolidBrush(_missing ? Color.FromArgb(90, Color.White) : Color.FromArgb(235, Color.White));
            g.FillPath(back, path);
            using var border = new Pen(_missing ? Color.FromArgb(170, 170, 170) : Color.FromArgb(150, 160, 175)) { DashStyle = _missing ? DashStyle.Dash : DashStyle.Solid };
            g.DrawPath(border, path);
        }

        var color = _missing ? Color.FromArgb(160, 160, 160) : Color.FromArgb(45, 60, 85);
        var icon = new RectangleF(Height * 0.22f, Height * 0.18f, Height * 1.1f, Height * 0.64f);
        DeviceArt.Draw(g, _kind, icon, color);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(icon.Right + (Height * 0.25f)), 0, Width - (int)icon.Right - Height / 4, Height),
            color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _down = e.Location;
        _pressed = e.Button == MouseButtons.Left;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var drag = SystemInformation.DragSize;
        if (_pressed && Item is not null && (Math.Abs(e.X - _down.X) > drag.Width || Math.Abs(e.Y - _down.Y) > drag.Height))
        {
            _pressed = false;
            DoDragDrop(Item, DragDropEffects.Move);
        }
    }
}

/// <summary>Base for drop targets: accepts a <see cref="DragItem"/> unless the view is locked.</summary>
internal abstract class DropPanel : Panel
{
    protected DropPanel(WorkplacesView view)
    {
        View = view;
        AllowDrop = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected WorkplacesView View { get; }

    protected abstract bool Accepts(DragItem item);

    protected abstract void OnDropItem(DragItem item);

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = !View.Locked && e.Data?.GetData(typeof(DragItem)) is DragItem item && Accepts(item) ? DragDropEffects.Move : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (!View.Locked && e.Data?.GetData(typeof(DragItem)) is DragItem item && Accepts(item))
        {
            OnDropItem(item);
        }
    }
}

/// <summary>Everything not given to a place: spare monitors, keyboards and mice.</summary>
internal sealed class SparesColumn : DropPanel
{
    private const int Header = 34;

    public SparesColumn(WorkplacesView view, IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<InputDevice> devices)
        : base(view)
    {
        Size = new Size(LogicalToDeviceUnits(178), LogicalToDeviceUnits(330));
        Margin = new Padding(LogicalToDeviceUnits(6));
        var list = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Location = new Point(LogicalToDeviceUnits(10), LogicalToDeviceUnits(Header + 6)),
            Size = new Size(Width - LogicalToDeviceUnits(20), Height - LogicalToDeviceUnits(Header + 16)),
            BackColor = Color.Transparent,
            AllowDrop = true,
        };
        list.DragEnter += (_, e) => OnDragEnter(e);
        list.DragDrop += (_, e) => OnDragDrop(e);

        foreach (var m in monitors)
        {
            list.Controls.Add(new DeviceChip(ChipKind.Monitor, $"Monitor {m.Number}", new DragItem(ChipKind.Monitor, m.Id, null, null), tip: m.Text));
        }

        foreach (var d in devices)
        {
            var kind = d.IsKeyboard ? ChipKind.Keyboard : ChipKind.Mouse;
            list.Controls.Add(new DeviceChip(kind, d.Description, new DragItem(kind, null, d, null), tip: d.HardwareId));
        }

        if (list.Controls.Count == 0)
        {
            list.Controls.Add(new Label
            {
                Text = "Nothing spare.\nEverything is given to a place.",
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(LogicalToDeviceUnits(4)),
            });
        }

        Controls.Add(list);
    }

    protected override bool Accepts(DragItem item) => item.From is not null;

    protected override void OnDropItem(DragItem item) => View.Drop(item, null);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var path = WorkplacesView.Rounded(r, LogicalToDeviceUnits(10)))
        {
            using var back = new SolidBrush(Color.FromArgb(226, 229, 238));
            g.FillPath(back, path);
            using var border = new Pen(Color.FromArgb(160, 166, 184), 1.5f);
            g.DrawPath(border, path);
        }

        var h = LogicalToDeviceUnits(Header);
        using var title = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, "Spares", title, new Rectangle(0, 0, Width, h), Color.FromArgb(45, 60, 85),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using var line = new Pen(Color.FromArgb(180, 186, 200));
        g.DrawLine(line, LogicalToDeviceUnits(10), h, Width - LogicalToDeviceUnits(10), h);
        TextRenderer.DrawText(g, "Drag onto a place", Font, new Rectangle(0, Height - LogicalToDeviceUnits(4) - Font.Height, Width, Font.Height),
            Color.Gray, TextFormatFlags.HorizontalCenter);
    }
}

/// <summary>One player's place: header with state, the monitor with its number, keyboard / mouse / headset.</summary>
internal sealed class PlaceCard : DropPanel
{
    private const int Header = 34;
    private readonly Seat _seat;
    private readonly int _number;
    private readonly MonitorInfo? _monitor;
    private readonly bool _local;
    private string _status = "stopped";
    private PlaceState _state = PlaceState.Stopped;
    private bool _selected;
    private Point _down;
    private bool _pressed;

    public PlaceCard(WorkplacesView view, Seat seat, int number, MonitorInfo? monitor, bool localMode)
        : base(view)
    {
        _seat = seat;
        _number = number;
        _monitor = monitor;
        _local = localMode;
        Size = new Size(LogicalToDeviceUnits(190), LogicalToDeviceUnits(330));
        Margin = new Padding(LogicalToDeviceUnits(6));
        Cursor = Cursors.Hand;

        var devices = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Location = new Point(LogicalToDeviceUnits(16), LogicalToDeviceUnits(178)),
            Size = new Size(LogicalToDeviceUnits(162), LogicalToDeviceUnits(112)),
            AllowDrop = true,
        };
        devices.DragEnter += (_, e) => OnDragEnter(e);
        devices.DragDrop += (_, e) => OnDragDrop(e);
        devices.Click += (_, _) => View.PlaceClicked(_seat);
        devices.DoubleClick += (_, _) => View.PlaceDoubleClicked(_seat);

        if (seat.IsConsole)
        {
            devices.Controls.Add(new DeviceChip(ChipKind.Keyboard, "other keyboards", null, tip: "Every keyboard not given to a place"));
            devices.Controls.Add(new DeviceChip(ChipKind.Mouse, "other mice", null, tip: "Every mouse not given to a place"));
            devices.Controls.Add(new DeviceChip(ChipKind.Headset, "Windows default", null));
        }
        else
        {
            devices.Controls.Add(Chip(ChipKind.Keyboard, seat.Keyboard, "no keyboard"));
            devices.Controls.Add(Chip(ChipKind.Mouse, seat.Mouse, "no mouse"));
            devices.Controls.Add(new DeviceChip(ChipKind.Headset, seat.AudioOutputName ?? "Windows default", null,
                missing: seat.AudioOutputId is null, tip: "Headset: double-click the place to choose"));
        }

        foreach (Control chip in devices.Controls)
        {
            chip.Click += (_, _) => View.PlaceClicked(_seat);
            chip.DoubleClick += (_, _) => View.PlaceDoubleClicked(_seat);
        }

        Controls.Add(devices);
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                Invalidate();
            }
        }
    }

    public void SetStatus(string text, PlaceState state)
    {
        if (text != _status || state != _state)
        {
            _status = text;
            _state = state;
            Invalidate();
        }
    }

    private DeviceChip Chip(ChipKind kind, InputDevice? device, string none) =>
        device is null
            ? new DeviceChip(kind, none, null, missing: true, tip: "Drag one here from Spares, or use Assign devices")
            : new DeviceChip(kind, device.Description, new DragItem(kind, null, device, _seat), tip: device.HardwareId);

    private RectangleF MonitorArea => new(LogicalToDeviceUnits(30), LogicalToDeviceUnits(Header + 14), LogicalToDeviceUnits(130), LogicalToDeviceUnits(96));

    protected override bool Accepts(DragItem item) =>
        !ReferenceEquals(item.From, _seat) && (item.Kind == ChipKind.Monitor || !_seat.IsConsole);

    protected override void OnDropItem(DragItem item) => View.Drop(item, _seat);

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        View.PlaceClicked(_seat);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        View.PlaceDoubleClicked(_seat);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _down = e.Location;
        _pressed = e.Button == MouseButtons.Left && _seat.Monitor is not null && MonitorArea.Contains(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            View.PlaceClicked(_seat); // the right-click menu acts on this place
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var drag = SystemInformation.DragSize;
        if (_pressed && (Math.Abs(e.X - _down.X) > drag.Width || Math.Abs(e.Y - _down.Y) > drag.Height))
        {
            _pressed = false;
            DoDragDrop(new DragItem(ChipKind.Monitor, _seat.Monitor, null, _seat), DragDropEffects.Move);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1.5f, 1.5f, Width - 4, Height - 4);
        using (var path = WorkplacesView.Rounded(r, LogicalToDeviceUnits(10)))
        {
            using var back = new SolidBrush(WorkplacesView.Background(_state, _seat.IsConsole));
            g.FillPath(back, path);
            using var border = new Pen(_selected ? Color.FromArgb(30, 100, 210) : Color.FromArgb(160, 166, 184), _selected ? 3.5f : 1.5f);
            g.DrawPath(border, path);
        }

        // Header: state dot + name.
        var h = LogicalToDeviceUnits(Header);
        var dot = LogicalToDeviceUnits(12);
        using (var dotBrush = new SolidBrush(WorkplacesView.Dot(_state)))
        {
            g.FillEllipse(dotBrush, LogicalToDeviceUnits(12), (h - dot) / 2f, dot, dot);
        }

        using var bold = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold);
        TextRenderer.DrawText(g, $"Place {_number}  ·  {_seat.Name}", bold,
            new Rectangle(LogicalToDeviceUnits(30), 0, Width - LogicalToDeviceUnits(38), h), Color.FromArgb(35, 45, 65),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using (var line = new Pen(Color.FromArgb(70, 0, 0, 0)))
        {
            g.DrawLine(line, LogicalToDeviceUnits(10), h, Width - LogicalToDeviceUnits(10), h);
        }

        // Monitor with its number (the number Identify monitors shows on the desk).
        var m = MonitorArea;
        var hasMonitor = _monitor is not null;
        var color = hasMonitor ? Color.FromArgb(40, 55, 80) : Color.FromArgb(150, 150, 150);
        DeviceArt.Draw(g, ChipKind.Monitor, m, color);
        var screen = new RectangleF(m.X + 4, m.Y + 4, m.Width - 8, (m.Height * 0.72f) - 8);
        using (var glow = new LinearGradientBrush(screen, hasMonitor ? Color.FromArgb(70, 130, 220) : Color.FromArgb(225, 225, 225),
                   hasMonitor ? Color.FromArgb(25, 70, 160) : Color.FromArgb(200, 200, 200), 90f))
        using (var path = WorkplacesView.Rounded(screen, 4))
        {
            g.FillPath(glow, path);
        }

        using (var big = new Font(Font.FontFamily, Font.Size * 2.6f, FontStyle.Bold))
        {
            var label = hasMonitor ? _monitor!.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
            TextRenderer.DrawText(g, label, big, Rectangle.Round(screen), hasMonitor ? Color.White : Color.FromArgb(150, 150, 150),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        var caption = hasMonitor
            ? _monitor!.Text
            : _local && !_seat.IsConsole ? "own screen (not on a monitor yet)" : "drag a monitor here";
        TextRenderer.DrawText(g, caption, Font, new Rectangle(LogicalToDeviceUnits(8), (int)m.Bottom + 2, Width - LogicalToDeviceUnits(16), Font.Height * 2),
            Color.FromArgb(70, 80, 100), TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        // Footer: status and who signs in.
        var who = _seat.IsConsole ? "this desktop" : _seat.UserName ?? (_local ? "player signs in at the seat" : "no Windows user");
        var footer = new Rectangle(LogicalToDeviceUnits(8), Height - LogicalToDeviceUnits(36), Width - LogicalToDeviceUnits(16), LogicalToDeviceUnits(32));
        TextRenderer.DrawText(g, $"{_status}\n{who}", Font, footer, Color.FromArgb(60, 70, 90),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Dashed "+ Add player" card at the end.</summary>
internal sealed class AddCard : Control
{
    private readonly WorkplacesView _view;

    public AddCard(WorkplacesView view)
    {
        _view = view;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(LogicalToDeviceUnits(150), LogicalToDeviceUnits(330));
        Margin = new Padding(LogicalToDeviceUnits(6));
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _view.RequestAdd();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? SystemColors.Control);
        using (var path = WorkplacesView.Rounded(new RectangleF(2, 2, Width - 5, Height - 5), LogicalToDeviceUnits(10)))
        using (var pen = new Pen(Color.FromArgb(150, 160, 180), 2) { DashStyle = DashStyle.Dash })
        {
            g.DrawPath(pen, path);
        }

        using var big = new Font(Font.FontFamily, Font.Size * 3, FontStyle.Bold);
        TextRenderer.DrawText(g, "+", big, new Rectangle(0, Height / 2 - LogicalToDeviceUnits(60), Width, LogicalToDeviceUnits(60)),
            Color.FromArgb(120, 135, 160), TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
        TextRenderer.DrawText(g, "Add player", Font, new Rectangle(0, Height / 2 + LogicalToDeviceUnits(4), Width, LogicalToDeviceUnits(24)),
            Color.FromArgb(90, 105, 130), TextFormatFlags.HorizontalCenter);
    }
}
