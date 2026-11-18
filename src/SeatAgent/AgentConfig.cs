using System.Text.Json;

namespace MultiSeat.Agent;

/// <summary>
/// C:\MultiSeat\seat.json inside the VM. Written by scripts/host/Update-SeatGuest.ps1.
/// </summary>
internal sealed class AgentConfig
{
    public const string DefaultPath = @"C:\MultiSeat\seat.json";

    public string Name { get; set; } = "Seat";

    public string BindAddress { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 47800;

    public string Secret { get; set; } = "";

    /// <summary>Release everything when no packet (including heartbeats) arrives for this long.</summary>
    public int WatchdogMs { get; set; } = 2000;

    public static AgentConfig Load(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException($"{path} is empty.");
        if (string.IsNullOrWhiteSpace(config.Secret))
        {
            throw new InvalidDataException($"{path} has no secret.");
        }

        return config;
    }
}
