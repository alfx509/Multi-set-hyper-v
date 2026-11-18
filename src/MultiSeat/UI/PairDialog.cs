using MultiSeat.Core;
using MultiSeat.Input;

namespace MultiSeat.UI;

/// <summary>"Press a key on the keyboard for this seat" / "Click the mouse for this seat".</summary>
internal sealed class PairDialog : Form
{
    private readonly InputRouter _router;
    private readonly bool _keyboard;
    private readonly CancellationTokenSource _cancel = new();

    public PairDialog(InputRouter router, bool keyboard, string seatName)
    {
        _router = router;
        _keyboard = keyboard;
        Text = keyboard ? "Assign keyboard" : "Assign mouse";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(460, 150);

        var label = new Label
        {
            Text = keyboard
                ? $"Press any key on the keyboard for \"{seatName}\".\n\n(Esc on any keyboard = cancel)"
                : $"Click the LEFT button of the mouse for \"{seatName}\".\n\n(Esc on any keyboard = cancel)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 11),
        };
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Bottom, Height = 32 };
        cancel.Click += (_, _) => _cancel.Cancel();
        Controls.Add(label);
        Controls.Add(cancel);
    }

    public InputDevice? Result { get; private set; }

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
