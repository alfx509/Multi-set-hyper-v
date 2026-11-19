using System.Diagnostics;
using System.Runtime.InteropServices;
using MultiSeat.Core;
using MultiSeat.Host;
using MultiSeat.Input;
using MultiSeat.Sessions;

namespace MultiSeat.UI;

/// <summary>The MultiSeat window: seats list, quick device assignment, Start/Stop, tray icon.</summary>
internal sealed class MainForm : Form
{
    private const int ColStatus = 8;

    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        Dock = DockStyle.Fill,
        GridLines = true,
    };

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        BackColor = SystemColors.Window,
    };

    private readonly Button _start = BigButton("▶  Start", Color.FromArgb(22, 128, 61));
    private readonly Button _stop = BigButton("■  Stop", Color.FromArgb(185, 28, 28));
    private readonly Button _add = ToolButton("➕  Add player");
    private readonly Button _assign = ToolButton("🎮  Assign devices…");
    private readonly Button _edit = ToolButton("✏  Edit…");
    private readonly Button _remove = ToolButton("✖  Remove");
    private readonly Button _identify = ToolButton("🖥  Identify monitors");
    private readonly Button _check = ToolButton("✔  Check this PC");
    private readonly Button _settings = ToolButton("⚙  Settings");

    private readonly ToolStripStatusLabel _summary = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly NotifyIcon _tray = new() { Text = "MultiSeat", Icon = AppIcon.Get() };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };

    private readonly MultiSeatConfig _config = LoadConfig();
    private readonly InputRouter? _router;
    private readonly Dictionary<Seat, SeatSession> _sessions = new();
    private readonly Dictionary<Seat, SeatChannel> _channels = new();
    private readonly Dictionary<Seat, int> _reopenCount = new();
    private readonly CursorFence _fence = new();
    private bool _fenceOn;
    private bool _running;
    private bool _starting;
    private int _tick;
    private Font _boldFont = SystemFonts.DefaultFont;
    private bool _startHidden;
    private bool _exiting;
    private bool _trayHintShown;

    /// <param name="autoStarted">True when Windows started MultiSeat at sign-in.</param>
    public MainForm(bool autoStarted = false)
    {
        Text = "MultiSeat – one PC, many players";
        Icon = AppIcon.Get();
        // Start hidden in the tray only once MultiSeat has been set up (the very first start always shows the window).
        _startHidden = autoStarted && _config.StartMinimized && File.Exists(AppPaths.Config);
        ClientSize = new Size(1180, 640);
        MinimumSize = new Size(900, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        _boldFont = new Font(Font, FontStyle.Bold);

        foreach (var (name, width) in new[]
                 {
                     ("#", 34), ("Player", 170), ("Windows user", 110), ("Monitor", 200), ("Keyboard", 95), ("Mouse", 85),
                     ("Headset", 160), ("Microphone", 140), ("Status", 120),
                 })
        {
            _list.Columns.Add(name, width);
        }

        BuildLayout();
        WireEvents();

        _router = InputRouter.TryCreate(out var routerError);
        if (_router is null)
        {
            Log("✖ " + routerError);
        }
        else
        {
            _router.Message += m => BeginInvoke(() => Log(m));
        }

        Log($"Welcome! Up to {MultiSeatConfig.MaxSeats} players. Add a player for each extra person, then use " +
            "\"Assign devices\" so everyone presses a key and clicks their own mouse. Then press Start.");
        Log("While playing: Ctrl+Alt+F12 on a player's keyboard lends it to the main desktop (press again to give it back).");
        RefreshList();
        _timer.Start();
    }

    // ------------------------------------------------------------------ layout

    private static Button BigButton(string text, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(120, 40),
        Font = new Font("Segoe UI", 12, FontStyle.Bold),
        ForeColor = Color.White,
        BackColor = color,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(4),
    };

    private static Button ToolButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(0, 40),
        Margin = new Padding(4),
    };

    private void BuildLayout()
    {
        var menu = new MenuStrip();
        var players = new ToolStripMenuItem("&Players");
        players.DropDownItems.Add("Add player", null, (_, _) => AddSeat());
        players.DropDownItems.Add("Assign devices for everyone…", null, (_, _) => AssignAll());
        players.DropDownItems.Add("Identify monitors", null, (_, _) => Monitors.Identify());
        players.DropDownItems.Add(new ToolStripSeparator());
        players.DropDownItems.Add("Sign out all players", null, (_, _) => SignOutAll());
        players.DropDownItems.Add(new ToolStripSeparator());
        players.DropDownItems.Add("Exit", null, (_, _) => ExitApp());

        var options = new ToolStripMenuItem("&Options");
        options.DropDownItems.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings(), Keys.Control | Keys.Oemcomma));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("Check this PC", null, (_, _) => ShowChecks(HostCheck.Run(_config), always: true));
        help.DropDownItems.Add("Open settings folder", null, (_, _) => OpenPath(AppPaths.Data));
        help.DropDownItems.Add("Guide (online)", null, (_, _) => OpenPath("https://github.com/alfx509/Multi-set-hyper-v/blob/main/docs/guide.md"));
        menu.Items.AddRange(new ToolStripItem[] { players, options, help });

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
        toolbar.Controls.AddRange(new Control[] { _start, _stop, Gap(), _add, _assign, _edit, _remove, Gap(), _identify, _check, _settings });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 360 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_log);

        var status = new StatusStrip();
        status.Items.Add(_summary);

        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(menu);
        Controls.Add(status);
        MainMenuStrip = menu;

        var rowMenu = new ContextMenuStrip();
        rowMenu.Items.Add("Edit…", null, (_, _) => EditSelected());
        rowMenu.Items.Add("Assign keyboard and mouse…", null, (_, _) => { if (Selected is { IsConsole: false } s) { Assign(new[] { s }); } });
        rowMenu.Items.Add("Reopen this player's screen", null, async (_, _) => { if (Selected is { } s) { await ReopenAsync(s, manual: true); } });
        rowMenu.Items.Add("Sign out this player", null, (_, _) => { if (Selected is { } s) { SignOut(s); } });
        rowMenu.Items.Add(new ToolStripSeparator());
        rowMenu.Items.Add("Remove", null, (_, _) => RemoveSelected());
        rowMenu.Opening += (_, e) =>
        {
            var seat = Selected;
            e.Cancel = seat is null;
            if (seat is null)
            {
                return;
            }

            rowMenu.Items[0].Enabled = !_running;
            rowMenu.Items[1].Enabled = !_running && !seat.IsConsole && _router is not null;
            rowMenu.Items[2].Enabled = _running && !seat.IsConsole;
            rowMenu.Items[3].Enabled = !seat.IsConsole;
            rowMenu.Items[5].Enabled = !_running && !seat.IsConsole;
        };
        _list.ContextMenuStrip = rowMenu;

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Show MultiSeat", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Start", null, async (_, _) => await StartAsync());
        trayMenu.Items.Add("Stop", null, (_, _) => Stop());
        trayMenu.Items.Add("Settings…", null, (_, _) => { ShowFromTray(); OpenSettings(); });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = trayMenu;
        _tray.Visible = true;
    }

    private static Control Gap() => new Label { Width = 16 };

    private void WireEvents()
    {
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => Stop();
        _add.Click += (_, _) => AddSeat();
        _assign.Click += (_, _) => AssignAll();
        _edit.Click += (_, _) => EditSelected();
        _remove.Click += (_, _) => RemoveSelected();
        _identify.Click += (_, _) => Monitors.Identify();
        _check.Click += (_, _) => ShowChecks(HostCheck.Run(_config), always: true);
        _settings.Click += (_, _) => OpenSettings();
        _list.DoubleClick += (_, _) => EditSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelected();
            }
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _timer.Tick += (_, _) => OnTick();

    }

    // ------------------------------------------------------------------ config + list

    private static MultiSeatConfig LoadConfig()
    {
        try
        {
            var config = MultiSeatConfig.Load(AppPaths.Config);
            config.Normalize();
            return config;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not read {AppPaths.Config}:\n{ex.Message}\n\nStarting with an empty setup.", "MultiSeat");
            return MultiSeatConfig.CreateDefault();
        }
    }

    private void SaveConfig()
    {
        try
        {
            SecureFile.Write(AppPaths.Config, _config.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"✖ Could not save settings: {ex.Message}");
        }
    }

    private void Log(string message) =>
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");

    private Seat? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as Seat : null;

    private void RefreshList()
    {
        var selected = Selected;
        _list.BeginUpdate();
        _list.Items.Clear();
        for (var i = 0; i < _config.Seats.Count; i++)
        {
            var seat = _config.Seats[i];
            var item = new ListViewItem((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                Tag = seat,
                UseItemStyleForSubItems = false,
            };
            item.SubItems.Add(seat.Name);
            item.SubItems.Add(seat.IsConsole ? "(you)" : seat.UserName ?? "–");
            item.SubItems.Add(Monitors.Describe(seat.Monitor));
            item.SubItems.Add(seat.IsConsole ? "all others" : seat.Keyboard?.Description ?? "⚠ none");
            item.SubItems.Add(seat.IsConsole ? "all others" : seat.Mouse?.Description ?? "⚠ none");
            item.SubItems.Add(seat.IsConsole ? "Windows default" : seat.AudioOutputName ?? "Windows default");
            item.SubItems.Add(seat.IsConsole ? "Windows default" : seat.AudioInputName ?? "Windows default");
            item.SubItems.Add("");
            item.Selected = ReferenceEquals(seat, selected);
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        UpdateStatuses();
        UpdateButtons();
    }

    private (string Text, Color Color) StatusOf(Seat seat)
    {
        if (_starting && !seat.IsConsole && !_sessions.ContainsKey(seat))
        {
            return ("starting…", Color.DarkOrange);
        }

        if (!_running)
        {
            return ("stopped", SystemColors.GrayText);
        }

        if (seat.IsConsole)
        {
            return ("playing", Color.ForestGreen);
        }

        if (!_sessions.TryGetValue(seat, out var session))
        {
            return ("not started", Color.Firebrick);
        }

        if (!session.IsRunning)
        {
            return ("screen closed", Color.Firebrick);
        }

        if (_channels.TryGetValue(seat, out var channel))
        {
            if (channel.HostMode)
            {
                return ("lent to desktop", Color.DarkOrange);
            }

            if (Environment.TickCount64 - channel.LastInput < 400)
            {
                return ("● playing", Color.ForestGreen);
            }
        }

        return ("playing", Color.ForestGreen);
    }

    private void UpdateStatuses()
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is Seat seat)
            {
                var (text, color) = StatusOf(seat);
                var cell = item.SubItems[ColStatus];
                if (cell.Text != text)
                {
                    cell.Text = text;
                    cell.ForeColor = color;
                    cell.Font = text.StartsWith('●') ? _boldFont : _list.Font;
                }
            }
        }

        var players = _config.Seats.Count;
        var state = _running ? "▶ Playing" : _starting ? "Starting…" : "Stopped";
        _summary.Text = $"Players: {players} / {MultiSeatConfig.MaxSeats}     Monitors: {Screen.AllScreens.Length}     {state}" +
                        (_router is null ? "     ⚠ input driver missing" : "");
        _tray.Text = $"MultiSeat – {state}";
    }

    private void UpdateButtons()
    {
        var seat = Selected;
        var idle = !_running && !_starting;
        _add.Enabled = idle && _config.Seats.Count < MultiSeatConfig.MaxSeats;
        _assign.Enabled = idle && _router is not null && _config.Seats.Count > 1;
        _edit.Enabled = idle && seat is not null;
        _remove.Enabled = idle && seat is { IsConsole: false };
        _start.Enabled = idle;
        _stop.Enabled = _running;
    }

    // ------------------------------------------------------------------ editing

    private void AddSeat()
    {
        if (_config.Seats.Count >= MultiSeatConfig.MaxSeats)
        {
            MessageBox.Show(this, $"MultiSeat supports up to {MultiSeatConfig.MaxSeats} players.", "MultiSeat");
            return;
        }

        var number = _config.Seats.Count + 1;
        var usedMonitors = _config.Seats.Select(s => s.Monitor).ToHashSet();
        var seat = new Seat
        {
            Name = $"Player {number}",
            UserName = $"player{number}",
            Monitor = Monitors.All().Select(s => s.DeviceName).FirstOrDefault(d => !usedMonitors.Contains(d)),
        };
        if (EditSeat(seat, isNew: true))
        {
            Log($"Added {seat.Name}.");
        }
    }

    private void EditSelected()
    {
        if (Selected is { } seat && !_running && !_starting)
        {
            EditSeat(seat, isNew: false);
        }
    }

    private bool EditSeat(Seat seat, bool isNew)
    {
        using var form = new SeatForm(seat, _router);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }

        if (isNew)
        {
            _config.Seats.Add(seat);
        }

        _config.Normalize();
        if (form.NewPassword is { } password && seat.LoopbackAddress is not null && seat.UserName is not null)
        {
            try
            {
                Credentials.Save(seat.LoopbackAddress, seat.UserName, password);
                Log($"{seat.Name}: password saved in Windows Credential Manager.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log($"✖ {seat.Name}: could not save the password: {ex.Message}");
            }
        }

        LogProblems();
        SaveConfig();
        RefreshList();
        return true;
    }

    private void LogProblems()
    {
        foreach (var problem in _config.Validate())
        {
            Log("⚠ " + problem);
        }
    }

    /// <summary>Every player in turn presses a key on their keyboard and clicks their mouse (prompt on their own monitor).</summary>
    private void AssignAll() => Assign(_config.Seats.Where(s => !s.IsConsole).ToList());

    private void Assign(IReadOnlyList<Seat> seats)
    {
        if (_router is null || seats.Count == 0)
        {
            return;
        }

        for (var i = 0; i < seats.Count; i++)
        {
            var seat = seats[i];
            var step = seats.Count > 1 ? $"{i + 1} of {seats.Count}" : null;
            var screen = Monitors.Find(seat.Monitor);

            // A device can belong to one player only: free the devices this player is about to replace.
            using (var kb = new PairDialog(_router, keyboard: true, seat.Name, step, screen))
            {
                if (kb.ShowDialog(this) == DialogResult.OK && kb.Result is { } device)
                {
                    ReleaseDevice(device);
                    seat.Keyboard = device;
                }
            }

            using (var mouse = new PairDialog(_router, keyboard: false, seat.Name, step, screen))
            {
                if (mouse.ShowDialog(this) == DialogResult.OK && mouse.Result is { } device)
                {
                    ReleaseDevice(device);
                    seat.Mouse = device;
                }
            }

            Log($"{seat.Name}: keyboard = {seat.Keyboard?.Description ?? "none"}, mouse = {seat.Mouse?.Description ?? "none"}");
        }

        LogProblems();
        SaveConfig();
        RefreshList();
    }

    private void ReleaseDevice(InputDevice device)
    {
        foreach (var other in _config.Seats)
        {
            if (other.Keyboard?.Slot == device.Slot)
            {
                other.Keyboard = null;
            }

            if (other.Mouse?.Slot == device.Slot)
            {
                other.Mouse = null;
            }
        }
    }

    private void RemoveSelected()
    {
        if (_running || _starting || Selected is not { IsConsole: false } seat)
        {
            return;
        }

        if (MessageBox.Show(this, $"Remove {seat.Name}?\n(The Windows user and its files are not deleted.)", "MultiSeat",
                MessageBoxButtons.YesNo) != DialogResult.Yes)
        {
            return;
        }

        if (seat.LoopbackAddress is not null)
        {
            Credentials.Delete(seat.LoopbackAddress);
        }

        if (seat.UserName is not null)
        {
            try
            {
                File.Delete(AgentSettings.PathFor(seat.UserName));
            }
            catch (IOException)
            {
            }
        }

        _config.Seats.Remove(seat);
        SaveConfig();
        RefreshList();
        Log($"Removed {seat.Name}.");
    }

    // ------------------------------------------------------------------ start / stop

    private bool ShowChecks(List<CheckResult> results, bool always)
    {
        foreach (var r in results)
        {
            Log((r.Level switch { CheckLevel.Ok => "✔ ", CheckLevel.Warning => "⚠ ", _ => "✖ " }) + r.Message);
        }

        var errors = results.Where(r => r.Level == CheckLevel.Error).ToList();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n\n", errors.Select(e => e.Message)), "MultiSeat – please fix first",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (always)
        {
            var warnings = results.Count(r => r.Level == CheckLevel.Warning);
            MessageBox.Show(this, warnings == 0 ? "This PC looks ready." : $"Ready, with {warnings} warning(s). See the log.", "MultiSeat");
        }

        return true;
    }

    private async Task StartAsync()
    {
        if (_running || _starting)
        {
            return;
        }

        var problems = _config.Validate().ToList();
        if (problems.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", problems), "MultiSeat – please fix first", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!ShowChecks(HostCheck.Run(_config), always: false))
        {
            return;
        }

        var seats = _config.Seats.Where(s => !s.IsConsole).ToList();
        if (seats.Count > 0 && _router is null)
        {
            MessageBox.Show(this, "The input driver is not available, so players cannot get their own keyboard and mouse.\nSee the log.", "MultiSeat");
            return;
        }

        // Devices can move to another slot after re-plugging; then they must be assigned again.
        var changed = seats
            .SelectMany(s => new[] { (s, s.Keyboard), (s, s.Mouse) })
            .Where(x => x.Item2 is not null && _router!.HardwareId(x.Item2.Slot) != x.Item2.HardwareId)
            .ToList();
        if (changed.Count > 0)
        {
            var names = string.Join(", ", changed.Select(x => $"{x.s.Name} ({x.Item2!.Description})"));
            if (MessageBox.Show(this, $"These devices were unplugged or moved: {names}.\n\nAssign devices again now?", "MultiSeat",
                    MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                Assign(changed.Select(x => x.s).Distinct().ToList());
            }

            return;
        }

        SaveConfig();
        _starting = true;
        _reopenCount.Clear();
        UpdateButtons();
        UpdateStatuses();

        try
        {
            foreach (var seat in seats)
            {
                SecureFile.WriteAgentSettings(seat);
                _channels[seat] = new SeatChannel(seat);
            }

            _router?.SetRoutes(_channels.Values);

            // Start all players' screens at the same time; signing in takes a few seconds each.
            await Task.WhenAll(seats.Select(s => LaunchAsync(s)));
            _running = true;
            if (_config.ConfineConsoleCursor && Monitors.Find(_config.Seats[0].Monitor) is null)
            {
                Log("⚠ Seat 1 (this desktop) has no monitor chosen, so your mouse can move onto players' screens. " +
                    "Edit Seat 1 and choose your monitor.");
            }

            UpdateFence(force: true);
            Log($"▶ {seats.Count + 1} players. Each keyboard and mouse now works only on its own screen.");
        }
        finally
        {
            _starting = false;
            UpdateButtons();
            UpdateStatuses();
        }
    }

    private async Task LaunchAsync(Seat seat)
    {
        var screen = Monitors.Find(seat.Monitor);
        if (screen is null)
        {
            Log($"✖ {seat.Name}: monitor {seat.Monitor} is not connected – skipped.");
            return;
        }

        try
        {
            Log($"{seat.Name}: signing in {seat.UserName} on {Monitors.Describe(seat.Monitor)}…");
            var session = await SessionLauncher.LaunchAsync(seat, screen, m => BeginInvoke(() => Log(m)));
            _sessions[seat] = session;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Log($"✖ {seat.Name}: could not start: {ex.Message}");
        }
    }

    private async Task ReopenAsync(Seat seat, bool manual)
    {
        if (!_running || seat.IsConsole)
        {
            return;
        }

        if (_sessions.Remove(seat, out var old))
        {
            old.Close();
        }

        Log(manual ? $"{seat.Name}: reopening the screen." : $"{seat.Name}: the screen closed – reopening it.");
        await LaunchAsync(seat);
    }

    private void Stop()
    {
        if (!_running && _channels.Count == 0 && _sessions.Count == 0)
        {
            return;
        }

        _router?.ClearRoutes();
        foreach (var c in _channels.Values)
        {
            c.Dispose();
        }

        _channels.Clear();
        foreach (var s in _sessions.Values)
        {
            s.Close();
        }

        _sessions.Clear();
        _running = false;
        UpdateFence(force: false);
        Log("■ Stopped. All keyboards and mice are back on the main desktop. Players stay signed in (games keep running) " +
            "until you use Players → Sign out all players.");
        UpdateButtons();
        UpdateStatuses();
    }

    private void SignOut(Seat seat)
    {
        if (seat.IsConsole || seat.UserName is null)
        {
            return;
        }

        if (MessageBox.Show(this, $"Sign out {seat.Name}? Unsaved games of this player will be closed.", "MultiSeat",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        if (_sessions.Remove(seat, out var session))
        {
            session.Close();
        }

        Log($"{seat.Name}: signed out {WtsSessions.SignOut(seat.UserName)} session(s).");
        UpdateStatuses();
    }

    private void SignOutAll()
    {
        if (MessageBox.Show(this, "Sign out every player? Their unsaved games will be closed.", "MultiSeat",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        Stop();
        foreach (var seat in _config.Seats.Where(s => !s.IsConsole && s.UserName is not null))
        {
            Log($"{seat.Name}: signed out {WtsSessions.SignOut(seat.UserName!)} session(s).");
        }
    }

    private async void OnTick()
    {
        _tick++;
        UpdateFence(force: false);

        if (_tick % 2 == 0)
        {
            UpdateStatuses();
        }

        if (_running && _config.AutoReconnect && _tick % 12 == 0)
        {
            foreach (var (seat, session) in _sessions.ToList())
            {
                if (!session.IsRunning)
                {
                    var count = _reopenCount.GetValueOrDefault(seat) + 1;
                    _reopenCount[seat] = count;
                    if (count <= 5)
                    {
                        await ReopenAsync(seat, manual: false);
                    }
                    else if (count == 6)
                    {
                        Log($"✖ {seat.Name}: the screen keeps closing. Right-click the player → Reopen, or check the password.");
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ window / tray

    /// <summary>When Windows starts MultiSeat at sign-in, the window stays hidden; only the tray icon shows.</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && value)
        {
            _startHidden = false;
            if (!IsHandleCreated)
            {
                CreateHandle();
            }

            value = false;
            BeginInvoke(() => _ = AutoStartSeatsAsync());
        }

        base.SetVisibleCore(value);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await AutoStartSeatsAsync();
    }

    private bool _autoStartDone;

    private async Task AutoStartSeatsAsync()
    {
        if (_autoStartDone || !_config.AutoStartSeats)
        {
            return;
        }

        _autoStartDone = true;
        Log("Starting the players automatically (Settings to turn off)…");
        await Task.Delay(3000);
        await StartAsync();
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_config);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SaveConfig();
        foreach (var m in form.Messages)
        {
            Log(m);
        }

        UpdateFence(force: true);
        Log("Settings saved.");
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _config.MinimizeToTray)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(3000, "MultiSeat", "MultiSeat is still running here. Double-click the icon to open it.", ToolTipIcon.Info);
        }
    }

    internal void ShowFromTray()
    {
        _startHidden = false;
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
    }

    private void ExitApp()
    {
        if (_running && MessageBox.Show(this, "Stop all players and exit MultiSeat?", "MultiSeat", MessageBoxButtons.YesNo) != DialogResult.Yes)
        {
            return;
        }

        _exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_exiting)
        {
            if (_config.CloseToTray)
            {
                // X button: keep running in the tray (exit from the tray menu or Players → Exit).
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (_running && MessageBox.Show(this, "Players are still playing. Stop all players and exit?", "MultiSeat",
                    MessageBoxButtons.YesNo) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        Stop();
        _timer.Stop();
        _fence.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _router?.Dispose();
        base.OnFormClosing(e);
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Keeps the main desktop's mouse on the main desktop's monitor while players play.</summary>
    private void UpdateFence(bool force)
    {
        var console = Monitors.Find(_config.Seats[0].Monitor);
        var want = _running && _config.ConfineConsoleCursor && console is not null;
        if (want && (!_fenceOn || force))
        {
            try
            {
                _fence.Enable(console!.Bounds);
                _fenceOn = true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log("✖ " + ex.Message);
                _config.ConfineConsoleCursor = false;
            }
        }
        else if (want)
        {
            _fence.Reclip();
        }
        else if (_fenceOn)
        {
            _fence.Disable();
            _fenceOn = false;
        }
    }
}
