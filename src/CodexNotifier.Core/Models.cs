using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace CodexNotifier.Core;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false)); File.Move(tmp, path, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}

public sealed class SourceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Home { get; set; } = ""; // Windows-readable path; configuration ownership is per canonical home.
    public string LinuxHome { get; set; } = "";
    public string Distro { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public bool IsWsl => !string.IsNullOrWhiteSpace(Distro);
    [JsonIgnore] public string Display => $"{(IsWsl ? "WSL · " + Distro : "Windows")}  ·  {Home}";
    public override string ToString() => Display;
}

public sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public bool AudioEnabled { get; set; } = true;
    public bool OrbVisible { get; set; } = true;
    public bool GlowEnabled { get; set; } = true;
    public bool ScreenFlash { get; set; }
    public double Volume { get; set; } = .65;
    public string AudioPath { get; set; } = "";
    public double OrbSize { get; set; } = 72;
    public double OrbLeft { get; set; } = 80;
    public double OrbTop { get; set; } = 160;
    public List<SourceProfile> Sources { get; set; } = [];
}

public sealed record TurnEvent(string SourceId, string ThreadId, string TurnId, string Kind, DateTimeOffset At, bool Subagent = false);
public sealed record EventResult(bool Changed, bool Notify, int Active, string Status);

public sealed class TurnTracker
{
    readonly HashSet<string> active = [];
    readonly HashSet<string> completed = [];
    readonly Queue<string> completionOrder = [];
    public int Active => active.Count;
    public EventResult Apply(TurnEvent e, bool enabled, bool replay = false)
    {
        if (e.Subagent || string.IsNullOrEmpty(e.ThreadId) || string.IsNullOrEmpty(e.TurnId)) return new(false, false, Active, "ignored");
        string key = e.SourceId + ":" + e.ThreadId + ":" + e.TurnId;
        if (completed.Contains(key)) return new(false, false, Active, "duplicate");
        if (e.Kind == "started") return new(active.Add(key), false, Active, "working");
        if (e.Kind is not ("completed" or "interrupted" or "failed")) return new(false, false, Active, "ignored");
        active.Remove(key);
        completed.Add(key); completionOrder.Enqueue(key);
        while (completionOrder.Count > 4096) completed.Remove(completionOrder.Dequeue());
        return new(true, enabled && !replay && e.Kind == "completed", Active, e.Kind);
    }
    public void Reset() { active.Clear(); completed.Clear(); completionOrder.Clear(); }
}

public static class EventParser
{
    public static string Str(JsonElement x, string key) => x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    public static TurnEvent? ParseRollout(string line, string source, ref string thread, ref bool child)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("payload", out var p)) return null;
        if (Str(root, "type") == "session_meta")
        {
            thread = Str(p, "id");
            // session source is either a string (vscode/cli) or a subagent descriptor.
            if (p.TryGetProperty("source", out var s)) child = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("subagent", out _);
            return null;
        }
        if (Str(root, "type") != "event_msg") return null;
        string kind = Str(p, "type") switch { "task_started" => "started", "task_complete" => "completed", "turn_aborted" => "interrupted", _ => "" };
        if (kind == "") return null;
        var turn = Str(p, "turn_id");
        var at = DateTimeOffset.TryParse(Str(root, "timestamp"), out var time) ? time : DateTimeOffset.UtcNow;
        return new(source, thread, turn, kind, at, child);
    }
    public static TurnEvent? ParseNotify(string payload, string source)
    {
        using var doc = JsonDocument.Parse(payload); var p = doc.RootElement;
        if (Str(p, "type") != "agent-turn-complete") return null;
        var thread = Str(p, "thread-id"); if (thread == "") thread = Str(p, "thread_id");
        var turn = Str(p, "turn-id"); if (turn == "") turn = Str(p, "turn_id");
        if (thread == "" || turn == "") return null; // let the explicit rollout completion handle older payloads.
        bool child = p.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("subagent", out _);
        return new(source, thread, turn, "completed", DateTimeOffset.UtcNow, child);
    }
}
