using System.Text.Json;

namespace GameShift.Core;

public sealed record GateCommand(int Version, string CommandId, bool Blocked, string Reason, DateTimeOffset UpdatedAt);
public sealed record GateAck(int Version, string CommandId, bool Blocked, int ActiveStreams, DateTimeOffset ObservedAt);

public static class ControlFiles
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameShift");
    public static void Write<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Settings.Json));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static T? Read<T>(string file)
    {
        if (!File.Exists(file)) return default;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 65536) throw new InvalidDataException("Control file is too large.");
        return JsonSerializer.Deserialize<T>(stream, Settings.Json);
    }
    public static bool CanUnload(GateCommand command, GateAck? ack, DateTimeOffset now) =>
        command.Version == 1 && !string.IsNullOrWhiteSpace(command.CommandId) && command.Blocked && ack is { Version: 1, Blocked: true, ActiveStreams: 0 } &&
        ack.CommandId == command.CommandId && ack.ObservedAt <= now.AddSeconds(2) &&
        now - ack.ObservedAt <= TimeSpan.FromSeconds(8);
}
