using MultiSeat.Host;
using MultiSeat.UI;

namespace MultiSeat;

internal static class Program
{
    private const string MutexName = @"Local\MultiSeat.App";
    private const string ShowEventName = @"Local\MultiSeat.Show";

    [STAThread]
    private static void Main(string[] args)
    {
        // Only one MultiSeat at a time: a second start just brings the running one to the front.
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var autoStarted = args.Any(a => string.Equals(a, StartupTask.AutoStartArgument, StringComparison.OrdinalIgnoreCase));

        ApplicationConfiguration.Initialize();
        using var form = new MainForm(autoStarted);

        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                if (form.IsDisposed)
                {
                    return;
                }

                try
                {
                    form.BeginInvoke(form.ShowFromTray);
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        })
        { IsBackground = true, Name = "MultiSeat show listener" };
        listener.Start();

        Application.Run(form);
    }
}
