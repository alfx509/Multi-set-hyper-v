using System.Runtime.InteropServices;
using Microsoft.Win32;
using MultiSeat.Core;
using MultiSeat.Host;

namespace MultiSeat.Sessions;

/// <summary>A running seat as the main window sees it: Remote Desktop window or MultiSeat Local session.</summary>
internal interface ISeatScreen
{
    Seat Seat { get; }

    bool IsRunning { get; }

    /// <summary>Takes the screen away; the player stays signed in (disconnected), so games keep their state.</summary>
    void Close();
}

/// <summary>A MultiSeat Local seat: its own session (own sign-in screen), its agent started by the MultiSeatSeats service.</summary>
internal sealed class LocalSeatSession : ISeatScreen
{
    public LocalSeatSession(Seat seat) => Seat = seat;

    public Seat Seat { get; }

    /// <summary>The seat's session right now (it changes when a player signs in to an existing, disconnected session).</summary>
    public int? SessionId => LocalSeatLauncher.ReadState().TryGetValue(LocalSeats.FileName(Seat), out var s) ? s.Session : null;

    public bool IsRunning => SessionId is not null;

    public void Close()
    {
        if (SessionId is int id)
        {
            WtsSessions.Disconnect(id);
        }
    }
}

/// <summary>
/// Starts MultiSeat Local seats (experimental, Phase 2). Needs the MultiSeat Local protocol provider (listener
/// "MultiSeat-Local") and the MultiSeatSeats service. Seats are connected one at a time: the service gives a new seat
/// session the first free agents\local-NN.json, so connecting in seat order keeps seat ↔ settings fixed.
/// Seat screens are not shown on monitors yet (presenter: needs the test PC).
/// </summary>
internal static class LocalSeatLauncher
{
    private const string ConnectEvent = @"Global\MultiSeatLocalConnect";
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static string StatePath => Path.Combine(AgentSettings.Directory, LocalSeats.StateFileName);

    /// <summary>Null when MultiSeat Local can be used, otherwise what is missing.</summary>
    public static string? Unavailable()
    {
        using (var listener = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\MultiSeat-Local"))
        {
            if (listener is null)
            {
                return "the MultiSeat Local protocol provider is not installed";
            }
        }

        using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MultiSeatSeats"))
        {
            if (service is null)
            {
                return "the MultiSeatSeats service is not installed";
            }
        }

        if (!EventWaitHandle.TryOpenExisting(ConnectEvent, out var connect))
        {
            return "the MultiSeat Local listener is not running (Remote Desktop Services)";
        }

        connect.Dispose();
        return null;
    }

    /// <summary>One settings file per seat (Administrators/SYSTEM only); files of removed seats are deleted.</summary>
    public static void WriteSettings(IReadOnlyCollection<Seat> seats)
    {
        Directory.CreateDirectory(AgentSettings.Directory);
        var wanted = seats.Select(LocalSeats.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(AgentSettings.Directory, "local-*.json"))
        {
            if (!wanted.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }

        foreach (var seat in seats)
        {
            var settings = new AgentSettings { Seat = seat.Name, Port = seat.AgentPort, Key = seat.AgentKey! };
            SecureFile.Write(
                Path.Combine(AgentSettings.Directory, LocalSeats.FileName(seat)),
                System.Text.Json.JsonSerializer.Serialize(settings, MultiSeatConfig.JsonOptions));
        }
    }

    public static Dictionary<string, (int Session, int AgentPid)> ReadState()
    {
        try
        {
            return LocalSeats.ParseState(File.ReadAllText(StatePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Connects the seat (if it is not connected yet) and waits until its agent runs.</summary>
    public static async Task<LocalSeatSession?> ConnectAsync(Seat seat, Action<string> log)
    {
        var file = LocalSeats.FileName(seat);
        await OneAtATime.WaitAsync();
        try
        {
            if (ReadState().TryGetValue(file, out var existing) && existing.AgentPid != 0)
            {
                return new LocalSeatSession(seat);
            }

            if (!EventWaitHandle.TryOpenExisting(ConnectEvent, out var connect))
            {
                log($"✖ {seat.Name}: the MultiSeat Local listener is not running.");
                return null;
            }

            using (connect)
            {
                connect.Set();
            }

            for (var i = 0; i < 80; i++)
            {
                await Task.Delay(250);
                if (ReadState().TryGetValue(file, out var s) && s.AgentPid != 0)
                {
                    log($"{seat.Name}: own session {s.Session} with its sign-in screen; input agent running.");
                    return new LocalSeatSession(seat);
                }
            }

            log($"✖ {seat.Name}: no seat session after 20 s (see C:\\ProgramData\\MultiSeat\\seatsvc.log and protocol.log).");
            return null;
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}
