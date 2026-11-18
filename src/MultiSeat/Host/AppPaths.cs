namespace MultiSeat.Host;

internal static class AppPaths
{
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MultiSeat");

    public static string Config => Path.Combine(Data, "config.json");

    public static string RdpDirectory => Path.Combine(Data, "rdp");

    public static string ClientsDirectory => Path.Combine(Data, "clients");

    public static string AgentExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MultiSeat", "Agent", "SeatAgent.exe");
}
