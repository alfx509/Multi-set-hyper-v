using MultiSeat.Core;
using MultiSeat.Input;

namespace MultiSeat.UI;

/// <summary>"Press a key on the keyboard for this seat" / "Click the mouse for this seat".</summary>
internal sealed class PairDialog : Form
{
    private readonly InputRouter _router;
    private readonly bool _keyboard;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Screen? _screen;

    public PairDialog(InputRouter router, bool keyboard, string seatName, string? step = null, Screen? screen = null)
    {
        _router = router;
        _keyboard = keyboard;
        Text = (keyboard ? "Assign keyboard" : "Assign mouse") + (step is null ? "" : $" – {step}");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        _screen = screen;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(520, 210);
        TopMost = true;

        var label = new Label
        {
            Text = keyboard
                ? $"{seatName}:\npress any key on YOUR keyboard.\n\n(Esc on any keyboard = skip)"
                : $"{seatName}:\nclick the LEFT button of YOUR mouse.\n\n(Esc on any keyboard = skip)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
        };
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Bottom, Height = 32 };
        cancel.Click += (_, _) => _cancel.Cancel();
        Controls.Add(label);
        Controls.Add(cancel);
    }

    public InputDevice? Result { get; private set; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Show the question on the player's own monitor, so they see it even with 8 screens.
        if (_screen is not null)
        {
            var b = _screen.WorkingArea;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(b.X + ((b.Width - Width) / 2), b.Y + ((b.Height - Height) / 2));
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Result = await _router.PairAsync(_keyboard, _cancel.Token);
        if (IsDisposed || !Visible)
        {
            return;
        }

        DialogResult = Result is null ? DialogResult.Cancel : DialogResult.OK;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cancel.Cancel();
        _cancel.Dispose();
        base.OnFormClosed(e);
    }
}
