using System.Runtime.InteropServices;
using MultiSeat.Core;
using MultiSeat.Host;
using MultiSeat.Input;
using MultiSeat.Sessions;

namespace MultiSeat.UI;

/// <summary>The MultiSeat window: list of seats, Add/Edit/Remove, Start/Stop.</summary>
internal sealed class MainForm : Form
{
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        Dock = DockStyle.Fill,
    };

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        BackColor = SystemColors.Window,
    };

    private readonly Button _add = new() { Text = "Add seat", AutoSize = true };
    private readonly Button _edit = new() { Text = "Edit…", AutoSize = true };
    private readonly Button _remove = new() { Text = "Remove", AutoSize = true };
    private readonly Button _identify = new() { Text = "Identify monitors", AutoSize = true };
    private readonly Button _check = new() { Text = "Check this PC", AutoSize = true };
    private readonly Button _start = new() { Text = "▶  Start", AutoSize = true, Font = new Font(DefaultFont.FontFamily, 11, FontStyle.Bold) };
    private readonly Button _stop = new() { Text = "■  Stop", AutoSize = true, Font = new Font(DefaultFont.FontFamily, 11, FontStyle.Bold) };
    private readonly Button _signOut = new() { Text = "Sign out seats", AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };

    private readonly MultiSeatConfig _config = LoadConfig();
    private readonly InputRouter? _router;
    private readonly List<SeatSession> _sessions = new();
    private readonly List<SeatChannel> _channels = new();
    private bool _running;

    public MainForm()
    {
        Text = "MultiSeat – one PC, many players";
        ClientSize = new Size(980, 560);
        MinimumSize = new Size(760, 420);
        StartPosition = FormStartPosition.CenterScreen;

        foreach (var (name, width) in new[]
                 {
                     ("Seat", 170), ("Windows user", 110), ("Monitor", 190), ("Keyboard", 90), ("Mouse", 80),
                     ("Headset", 150), ("Microphone", 120), ("Status", 90),
                 })
        {
            _list.Columns.Add(name, width);
        }

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        toolbar.Controls.AddRange(new Control[] { _start, _stop, Spacer(), _add, _edit, _remove, Spacer(), _identify, _check, _signOut });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_log);
        Controls.Add(split);
        Controls.Add(toolbar);

        _add.Click += (_, _) => AddSeat();
        _edit.Click += (_, _) => EditSelected();
        _list.DoubleClick += (_, _) => EditSelected();
        _remove.Click += (_, _) => RemoveSelected();
        _identify.Click += (_, _) => Monitors.Identify();
        _check.Click += (_, _) => ShowChecks(HostCheck.Run(_config), always: true);
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => Stop();
        _signOut.Click += (_, _) => SignOut();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _timer.Tick += (_, _) => OnTick();

        _router = InputRouter.TryCreate(out var routerError);
        if (_router is null)
        {
            Log(routerError!);
        }
        else
        {
            _router.Message += m => BeginInvoke(() => Log(m));
        }

        Log("Welcome. Add a seat for each extra player, assign their monitor, keyboard, mouse and headset, then press Start.");
        Log("While playing: Ctrl+Alt+F12 on a player's keyboard lends that keyboard and mouse to the main desktop (press again to go back).");
        RefreshList();
        _timer.Start();
    }

    private static Control Spacer() => new Label { Width = 18 };

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

    private void SaveConfig() => SecureFile.Write(AppPaths.Config, _config.ToJson());

    private void Log(string message) =>
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");

    private Seat? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as Seat : null;

    private void RefreshList()
    {
        var selected = Selected;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var seat in _config.Seats)
        {
            var item = new ListViewItem(seat.Name) { Tag = seat };
            item.SubItems.Add(seat.IsConsole ? "(you)" : seat.UserName ?? "-");
            item.SubItems.Add(Monitors.Describe(seat.Monitor));
            item.SubItems.Add(seat.IsConsole ? "all others" : seat.Keyboard?.Description ?? "-");
            item.SubItems.Add(seat.IsConsole ? "all others" : seat.Mouse?.Description ?? "-");
            item.SubItems.Add(seat.IsConsole ? "default" : seat.AudioOutputName ?? "default");
            item.SubItems.Add(seat.IsConsole ? "default" : seat.AudioInputName ?? "default");
            item.SubItems.Add(StatusOf(seat));
            item.Selected = ReferenceEquals(seat, selected);
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        UpdateButtons();
    }

    private string StatusOf(Seat seat)
    {
        if (!_running)
        {
            return "stopped";
        }

        if (seat.IsConsole)
        {
            return "playing";
        }

        var session = _sessions.FirstOrDefault(s => ReferenceEquals(s.Seat, seat));
        return session is null ? "not started" : session.IsRunning ? "playing" : "window closed";
    }

    private void UpdateButtons()
    {
        var seat = Selected;
        _add.Enabled = !_running;
        _edit.Enabled = !_running && seat is not null;
        _remove.Enabled = !_running && seat is { IsConsole: false };
        _start.Enabled = !_running;
        _stop.Enabled = _running;
        _signOut.Enabled = !_running;
    }

    private void AddSeat()
    {
        var number = _config.Seats.Count + 1;
        var seat = new Seat { Name = $"Seat {number}", UserName = $"player{number}" };
        if (EditSeat(seat, isNew: true))
        {
            Log($"Added {seat.Name}.");
        }
    }

    private void EditSelected()
    {
        if (Selected is { } seat && !_running)
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
                Log($"{seat.Name}: could not save the password: {ex.Message}");
            }
        }

        foreach (var problem in _config.Validate())
        {
            Log("⚠ " + problem);
        }

        SaveConfig();
        RefreshList();
        return true;
    }

    private void RemoveSelected()
    {
        if (Selected is not { IsConsole: false } seat)
        {
            return;
        }

        if (MessageBox.Show(this, $"Remove {seat.Name}? (The Windows user and its files are not deleted.)", "MultiSeat",
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
            MessageBox.Show(this, "This PC looks ready. Details are in the log.", "MultiSeat");
        }

        return true;
    }

    private async Task StartAsync()
    {
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
            MessageBox.Show(this, "The input driver is not available, so seats cannot get their own keyboard and mouse.\nSee the log.", "MultiSeat");
            return;
        }

        // Devices can move to another slot after re-plugging; then they must be assigned again.
        foreach (var seat in seats)
        {
            foreach (var d in new[] { seat.Keyboard, seat.Mouse })
            {
                if (d is not null && _router!.HardwareId(d.Slot) != d.HardwareId)
                {
                    MessageBox.Show(this, $"{seat.Name}: {d.Description} has changed (unplugged or moved). Edit the seat and assign it again.", "MultiSeat");
                    return;
                }
            }
        }

        SaveConfig();
        _running = true;
        UpdateButtons();

        foreach (var seat in seats)
        {
            SecureFile.WriteAgentSettings(seat);
            _channels.Add(new SeatChannel(seat));
        }

        _router?.SetRoutes(_channels);

        foreach (var seat in seats)
        {
            var screen = Monitors.Find(seat.Monitor);
            if (screen is null)
            {
                Log($"{seat.Name}: monitor {seat.Monitor} is not connected – skipped.");
                continue;
            }

            try
            {
                Log($"{seat.Name}: signing in {seat.UserName} on {Monitors.Describe(seat.Monitor)}…");
                _sessions.Add(await SessionLauncher.LaunchAsync(seat, screen, m => BeginInvoke(() => Log(m))));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Log($"{seat.Name}: could not start: {ex.Message}");
            }
        }

        Log("All seats started. Each player's keyboard and mouse now work only on their own screen.");
        RefreshList();
    }

    private void Stop()
    {
        _router?.ClearRoutes();
        foreach (var c in _channels)
        {
            c.Dispose();
        }

        _channels.Clear();
        foreach (var s in _sessions)
        {
            s.Close();
        }

        _sessions.Clear();
        ClipCursor(IntPtr.Zero);
        _running = false;
        Log("Stopped. All keyboards and mice are back on the main desktop. Seat users stay signed in (games keep running) until you press \"Sign out seats\".");
        RefreshList();
    }

    private void SignOut()
    {
        if (MessageBox.Show(this, "Sign out every seat user? Unsaved games in the seats will be closed.", "MultiSeat",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        foreach (var seat in _config.Seats.Where(s => !s.IsConsole && s.UserName is not null))
        {
            var n = WtsSessions.SignOut(seat.UserName!);
            Log($"{seat.Name}: signed out {n} session(s).");
        }
    }

    private int _tick;

    private void OnTick()
    {
        if (!_running)
        {
            return;
        }

        // Keep the main desktop's mouse on its own monitor, so it cannot wander onto a player's screen.
        if (_config.ConfineConsoleCursor && Monitors.Find(_config.Seats[0].Monitor) is { } console)
        {
            var b = console.Bounds;
            var rect = new NativeRect { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
            ClipCursor(ref rect);
        }

        if (++_tick % 4 == 0)
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is Seat seat)
                {
                    var status = StatusOf(seat);
                    if (item.SubItems[^1].Text != status)
                    {
                        item.SubItems[^1].Text = status;
                    }
                }
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running)
        {
            Stop();
        }

        _timer.Stop();
        _router?.Dispose();
        base.OnFormClosing(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr rect);
}
