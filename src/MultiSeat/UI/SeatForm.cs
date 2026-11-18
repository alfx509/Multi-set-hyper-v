using MultiSeat.Audio;
using MultiSeat.Core;
using MultiSeat.Input;

namespace MultiSeat.UI;

/// <summary>Edit one seat: name, Windows user, monitor, keyboard, mouse, headset, microphone, USB devices.</summary>
internal sealed class SeatForm : Form
{
    private readonly Seat _seat;
    private readonly InputRouter? _router;
    private readonly TextBox _name = new() { Width = 300 };
    private readonly TextBox _user = new() { Width = 300 };
    private readonly TextBox _password = new() { Width = 300, UseSystemPasswordChar = true };
    private readonly ComboBox _monitor = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _keyboard = new() { AutoSize = true };
    private readonly Label _mouse = new() { AutoSize = true };
    private readonly ComboBox _output = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _input = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _usb = new() { Width = 300, Height = 60, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private InputDevice? _keyboardDevice;
    private InputDevice? _mouseDevice;

    public SeatForm(Seat seat, InputRouter? router)
    {
        _seat = seat;
        _router = router;
        _keyboardDevice = seat.Keyboard;
        _mouseDevice = seat.Mouse;

        Text = seat.IsConsole ? "Main desktop seat" : "Seat";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) });
            grid.Controls.Add(control);
        }

        Row("Seat name", _name);
        Row("Monitor", WithButton(_monitor, "Identify", (_, _) => Monitors.Identify()));

        if (seat.IsConsole)
        {
            grid.Controls.Add(new Label
            {
                Text = "This is the normal Windows desktop. It uses every keyboard and mouse\n" +
                       "that is not assigned to another seat, and the Windows default sound device.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 10, 3, 3),
            });
            grid.SetColumnSpan(grid.Controls[^1], 2);
        }
        else
        {
            Row("Windows user", _user);
            Row("Password", _password);
            Row("Keyboard", WithPair(_keyboard, keyboard: true));
            Row("Mouse", WithPair(_mouse, keyboard: false));
            Row("Headset / speakers", _output);
            Row("Microphone", _input);
            Row("USB devices\n(experimental:\ngamepads etc.)", _usb);
            grid.Controls.Add(new Label
            {
                Text = "Password is saved in Windows Credential Manager, not in MultiSeat's files.\n" +
                       "Leave it empty to keep the saved one. USB devices: one Device Manager\n" +
                       "\"Device instance path\" per line; the device then belongs to this seat.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 10, 3, 3),
            });
            grid.SetColumnSpan(grid.Controls[^1], 2);
        }

        var ok = new Button { Text = "OK", DialogResult = DialogResult.None, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        ok.Click += (_, _) => Accept();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(grid);
        root.Controls.Add(buttons);
        Controls.Add(root);

        LoadValues();
    }

    /// <summary>Password typed in this dialog (null = keep the saved one).</summary>
    public string? NewPassword { get; private set; }

    private void LoadValues()
    {
        _name.Text = _seat.Name;
        _user.Text = _seat.UserName ?? "";

        var screens = Monitors.All();
        _monitor.Items.Add(new Choice(null, "(none)"));
        for (var i = 0; i < screens.Count; i++)
        {
            _monitor.Items.Add(new Choice(screens[i].DeviceName, Monitors.Describe(screens[i], i)));
        }

        Select(_monitor, _seat.Monitor);

        if (!_seat.IsConsole)
        {
            FillAudio(_output, capture: false, _seat.AudioOutputId);
            FillAudio(_input, capture: true, _seat.AudioInputId);
            _usb.Text = string.Join(Environment.NewLine, _seat.UsbDevices);
            ShowDevices();
        }
    }

    private static void FillAudio(ComboBox box, bool capture, string? selected)
    {
        box.Items.Add(new Choice(null, "(Windows default)"));
        try
        {
            foreach (var d in AudioDevices.List(capture))
            {
                box.Items.Add(new Choice(d.Id, d.Name));
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // No audio service: leave only the default entry.
        }

        Select(box, selected);
    }

    private static void Select(ComboBox box, string? id)
    {
        box.SelectedIndex = 0;
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is Choice c && c.Id == id)
            {
                box.SelectedIndex = i;
            }
        }
    }

    private void ShowDevices()
    {
        _keyboard.Text = _keyboardDevice?.Description ?? "(none)";
        _mouse.Text = _mouseDevice?.Description ?? "(none)";
    }

    private Control WithButton(Control control, string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += click;
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        panel.Controls.Add(control);
        panel.Controls.Add(button);
        return panel;
    }

    private Control WithPair(Label label, bool keyboard)
    {
        var assign = new Button { Text = keyboard ? "Assign (press a key)…" : "Assign (click)…", AutoSize = true, Enabled = _router is not null };
        var clear = new Button { Text = "Clear", AutoSize = true };
        assign.Click += (_, _) =>
        {
            using var dialog = new PairDialog(_router!, keyboard, string.IsNullOrWhiteSpace(_name.Text) ? "this seat" : _name.Text);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } device)
            {
                if (keyboard)
                {
                    _keyboardDevice = device;
                }
                else
                {
                    _mouseDevice = device;
                }

                ShowDevices();
            }
        };
        clear.Click += (_, _) =>
        {
            if (keyboard)
            {
                _keyboardDevice = null;
            }
            else
            {
                _mouseDevice = null;
            }

            ShowDevices();
        };
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        label.Margin = new Padding(3, 8, 12, 3);
        label.MinimumSize = new Size(90, 0);
        panel.Controls.Add(label);
        panel.Controls.Add(assign);
        panel.Controls.Add(clear);
        return panel;
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, "Give the seat a name.", Text);
            return;
        }

        if (!_seat.IsConsole && !System.Text.RegularExpressions.Regex.IsMatch(_user.Text.Trim(), @"^[A-Za-z0-9._-]{1,20}$"))
        {
            MessageBox.Show(this, "Type the seat's local Windows user name (letters, digits, . _ -).", Text);
            return;
        }

        _seat.Name = _name.Text.Trim();
        _seat.Monitor = (_monitor.SelectedItem as Choice)?.Id;
        if (!_seat.IsConsole)
        {
            _seat.UserName = _user.Text.Trim();
            _seat.Keyboard = _keyboardDevice;
            _seat.Mouse = _mouseDevice;
            var output = _output.SelectedItem as Choice;
            var input = _input.SelectedItem as Choice;
            _seat.AudioOutputId = output?.Id;
            _seat.AudioOutputName = output?.Id is null ? null : output.Text;
            _seat.AudioInputId = input?.Id;
            _seat.AudioInputName = input?.Id is null ? null : input.Text;
            _seat.UsbDevices = _usb.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            NewPassword = _password.Text.Length > 0 ? _password.Text : null;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record Choice(string? Id, string Text)
    {
        public override string ToString() => Text;
    }
}
