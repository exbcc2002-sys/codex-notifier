using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexNotifier.Core;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length >= 1 && args[0] == "--ping") return await Ipc.Send(new("ping"), 1200, args.Length == 2 ? args[1] : null) ? 0 : 2;
            if (args.Length == 2 && args[0] == "--event")
            {
                if (args[1].Length > 12000) return 0;
                var ev = JsonSerializer.Deserialize<TurnEvent>(Encoding.UTF8.GetString(Convert.FromBase64String(args[1])), JsonStore.Options);
                if (ev != null) await Ipc.Send(new("event", ev));
                return 0;
            }
            int at = Array.IndexOf(args, "--record");
            if (at < 0 || at + 1 >= args.Length || args.Length < 4) return 0;
            var r = JsonStore.Read<IntegrationRecord>(args[at + 1]);
            if (r is null || r.Status == "removed") return 0;
            var payload = args[^1];
            if (r.ForwardPrevious && r.PreviousNotify.Length > 0)
            {
                try
                {
                    var psi = new ProcessStartInfo(r.PreviousNotify[0]) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (var arg in r.PreviousNotify.Skip(1)) psi.ArgumentList.Add(arg);
                    psi.ArgumentList.Add(payload); Process.Start(psi)?.Dispose();
                }
                catch { /* old callback failure must not block the Codex turn */ }
            }
            var e = EventParser.ParseNotify(payload, r.SourceId);
            if (e != null) await Ipc.Send(new("event", e));
        }
        catch { /* relay never changes Codex success/failure and never writes conversation data */ }
        return 0;
    }
}
