using System.Diagnostics;
using MultiSeat.Core;
using MultiSeat.Host;

namespace MultiSeat.UI;

/// <summary>Settings: Windows startup, tray behaviour, player options, frame rate, help.</summary>
internal sealed class SettingsForm : Form
{
    private readonly MultiSeatConfig _config;

    private readonly CheckBox _startWithWindows = Check("Start MultiSeat when I sign in to Windows");
    private readonly CheckBox _startMinimized = Check("…and start hidden in the tray (notification area)");
    private readonly CheckBox _minimizeToTray = Check("Minimize to the tray instead of the taskbar");
    private readonly CheckBox _closeToTray = Check("Close button (X) keeps MultiSeat running in the tray");
    private readonly CheckBox _autoStartSeats = Check("Start the players automatically when MultiSeat opens");
    private readonly CheckBox _reconnect = Check("Reopen a player's screen if it closes");
    private readonly CheckBox _confine = Check("Keep my mouse on my own monitor while playing");
    private readonly ComboBox _fps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };

    public SettingsForm(MultiSeatConfig config)
    {
        _config = config;
        Text = "MultiSeat – Settings";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = new Font("Segoe UI", 9.5f);

        _fps.Items.AddRange(new object[] { "60 fps (smooth, more GPU)", "30 fps (lighter)" });

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        root.Controls.Add(Group("Windows startup", _startWithWindows, _startMinimized));
        root.Controls.Add(Group("Window", _minimizeToTray, _closeToTray));
        root.Controls.Add(Group("Players", _autoStartSeats, _reconnect, _confine, Row("Players' screens:", _fps)));

        var help = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(520, 0) };
        help.Controls.Add(LinkButton("Open settings folder", () => Open(AppPaths.Data)));
        help.Controls.Add(LinkButton("Open setup log", () => Open(Path.Combine(AppPaths.Data, "setup.log"))));
        help.Controls.Add(LinkButton("Open user guide", () => Open(Path.Combine(AppContext.BaseDirectory, "docs", "guide.md"))));
        var hotkey = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Ctrl + Alt + F12 on a player's keyboard lends that keyboard and mouse to the main desktop; " +
                   "press it again to give them back.",
        };
        root.Controls.Add(Group("Help", hotkey, help));

        var ok = new Button { Text = "OK", Width = 100 };
        var cancel = new Button { Text = "Cancel", Width = 100, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Save();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(root);

        _startWithWindows.CheckedChanged += (_, _) => _startMinimized.Enabled = _startWithWindows.Checked;
        LoadValues();
    }

    /// <summary>Messages for the main window's log.</summary>
    public List<string> Messages { get; } = new();

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };

    private static GroupBox Group(string title, params Control[] controls)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        panel.Controls.AddRange(controls);
        var box = new GroupBox { Text = title, AutoSize = true, MinimumSize = new Size(540, 0), Padding = new Padding(10, 6, 10, 8) };
        box.Controls.Add(panel);
        return box;
    }

    private static Control Row(string label, Control control)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
        row.Controls.Add(control);
        return row;
    }

    private static Button LinkButton(string text, Action action)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => action();
        return b;
    }

    private static void Open(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show($"Not found:\n{path}", "MultiSeat");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(ex.Message, "MultiSeat");
        }
    }

    private void LoadValues()
    {
        _startWithWindows.Checked = StartupTask.Exists();
        _startMinimized.Checked = _config.StartMinimized;
        _startMinimized.Enabled = _startWithWindows.Checked;
        _minimizeToTray.Checked = _config.MinimizeToTray;
        _closeToTray.Checked = _config.CloseToTray;
        _autoStartSeats.Checked = _config.AutoStartSeats;
        _reconnect.Checked = _config.AutoReconnect;
        _confine.Checked = _config.ConfineConsoleCursor;
        _fps.SelectedIndex = FrameRate.Get() >= 60 ? 0 : 1;
    }

    private void Save()
    {
        _config.StartMinimized = _startMinimized.Checked;
        _config.MinimizeToTray = _minimizeToTray.Checked;
        _config.CloseToTray = _closeToTray.Checked;
        _config.AutoStartSeats = _autoStartSeats.Checked;
        _config.AutoReconnect = _reconnect.Checked;
        _config.ConfineConsoleCursor = _confine.Checked;

        if (_startWithWindows.Checked != StartupTask.Exists())
        {
            var ok = _startWithWindows.Checked ? StartupTask.Enable() : StartupTask.Disable();
            Messages.Add(ok
                ? _startWithWindows.Checked ? "MultiSeat will start when you sign in to Windows." : "MultiSeat will no longer start with Windows."
                : "✖ Could not change the Windows startup task.");
        }

        var fps = _fps.SelectedIndex == 0 ? 60 : 30;
        if (fps != FrameRate.Get())
        {
            try
            {
                FrameRate.Set(fps);
                _config.FrameRate = fps;
                Messages.Add($"Players' screens: {fps} fps (applies to screens opened from now on).");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Messages.Add($"✖ Could not change the frame rate: {ex.Message}");
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>The MultiSeat icon, taken from MultiSeat.exe.</summary>
internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon Get() =>
        _icon ??= Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath) ?? SystemIcons.Application;
}
