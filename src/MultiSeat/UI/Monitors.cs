namespace MultiSeat.UI;

/// <summary>Monitors numbered left to right, the way people see them on the desk.</summary>
internal static class Monitors
{
    public static IReadOnlyList<Screen> All() =>
        Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();

    public static Screen? Find(string? deviceName) =>
        deviceName is null ? null : Screen.AllScreens.FirstOrDefault(s => s.DeviceName == deviceName);

    public static string Describe(string? deviceName)
    {
        var all = All();
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].DeviceName == deviceName)
            {
                return Describe(all[i], i);
            }
        }

        return deviceName is null ? "-" : $"{deviceName} (not connected)";
    }

    public static string Describe(Screen s, int index) =>
        $"Monitor {index + 1} – {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (main)" : "")}";

    /// <summary>Shows a big number on every monitor for a few seconds.</summary>
    public static void Identify()
    {
        var all = All();
        for (var i = 0; i < all.Count; i++)
        {
            var b = all[i].Bounds;
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(b.X + (b.Width / 2) - 150, b.Y + (b.Height / 2) - 150, 300, 300),
                BackColor = Color.FromArgb(30, 90, 200),
                TopMost = true,
                ShowInTaskbar = false,
            };
            form.Controls.Add(new Label
            {
                Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                Font = new Font(FontFamily.GenericSansSerif, 120, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
            });
            var timer = new System.Windows.Forms.Timer { Interval = 3000 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                form.Close();
            };
            form.Show();
            timer.Start();
        }
    }
}
