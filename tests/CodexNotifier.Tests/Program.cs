using CodexNotifier.Core;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

int probeIndex = Array.IndexOf(args, "--probe-home");
if (probeIndex >= 0)
{
    string turn = args[Array.IndexOf(args, "--probe-turn") + 1];
    var detected = new TaskCompletionSource<TurnEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var probe = new SessionMonitor([new SourceProfile { Id = "probe", Home = args[probeIndex + 1] }],
        (e, _) => { if (e.TurnId == turn && !e.Subagent) detected.TrySetResult(e); }, Console.WriteLine);
    probe.Start();
    var observed = await detected.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Console.WriteLine($"PASS real session probe: {observed.Kind}; main-thread identity resolved; no notification emitted.");
    return;
}

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
void Throws(Action action, string message) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, message); }
string temp = Path.Combine(Path.GetTempPath(), "CodexNotifierTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    string original = "# preserve me\r\nmodel = 'example'\r\nnotify = [\r\n  'original.exe', # keep original comment\r\n  '--with-space', 'a b'\r\n]\r\n[features]\r\nfast_mode = true\r\n";
    var command = new[] { @"C:\Users\测试 用户\Relay.exe", "--record", @"C:\path with spaces\integration.json" };
    Check(NotifyConfig.Read(NotifyConfig.Set("", ["C:\\提示🔔\\relay.exe"])).Single() == "C:\\提示🔔\\relay.exe", "supplementary Unicode filenames round-trip in TOML");
    var patched = NotifyConfig.Set(original, command);
    Check(NotifyConfig.Read(patched).SequenceEqual(command), "TOML Unicode / Windows paths round-trip");
    Check(patched.EndsWith("[features]\r\nfast_mode = true\r\n"), "unrelated tables preserved byte-for-byte");
    Check(NotifyConfig.Restore(patched, NotifyConfig.Find(original)?.Raw) == original, "multiline notify with comments restored exactly");
    string multiline = "description = '''\nnotify = [\"fake\"]\n[not_a_table]\n'''\nmodel = \"other\"\n[features]\nfast_mode = false\n";
    string added = NotifyConfig.Set(multiline, command);
    Check(NotifyConfig.Read(added).SequenceEqual(command), "fake notify inside multiline string is not replaced");
    Check(NotifyConfig.Restore(added, null) == multiline, "new root notify removed without damaging multiline content");
    string quoted = "\"notify\" = ['a']\n[section]\nnotify=['nested']\n";
    Check(NotifyConfig.Read(NotifyConfig.Set(quoted, command)).SequenceEqual(command), "quoted root key handled independently of nested notify");
    Throws(() => NotifyConfig.Set("notify='invalid'", command), "invalid existing notify rejected");
    Throws(() => NotifyConfig.Set("model = [", command), "malformed TOML rejected without writes");
    string home = Path.Combine(temp, "home"); Directory.CreateDirectory(home);
    string config = Path.Combine(home, "config.toml"); File.WriteAllText(config, original);
    var source = new SourceProfile { Id = "test", Home = home };
    var installed = Integration.Deploy(source, @"C:\relay.exe", true);
    Check(installed.PreviousNotify.SequenceEqual(new[] { "original.exe", "--with-space", "a b" }), "previous callback saved as argument array");
    string deployedText = File.ReadAllText(config);
    Integration.Deploy(source, @"C:\relay.exe", false);
    Check(File.ReadAllText(config) == deployedText, "repeated deployment is idempotent");
    Check(JsonStore.Read<IntegrationRecord>(Integration.RecordPath(source))!.ForwardPrevious == false, "forwarding setting updated independently");
    File.AppendAllText(config, "\n[custom]\nkeep = 'new user setting'\n");
    Integration.Remove(source);
    Check(File.ReadAllText(config) == original + "\n[custom]\nkeep = 'new user setting'\n", "removal keeps changes made after deployment");
    Integration.Remove(source); Check(true, "repeated removal is harmless");
    Integration.Deploy(source, @"C:\relay.exe", true);
    File.WriteAllText(config, NotifyConfig.Set(File.ReadAllText(config), ["new-owner.exe"]));
    var changed = File.ReadAllText(config);
    Throws(() => Integration.Remove(source), "foreign callback changes block removal");
    Check(File.ReadAllText(config) == changed, "conflict leaves current config intact");
    var wsl = new SourceProfile { Id = "linux", Home = Path.Combine(temp, "linux"), Distro = "Ubuntu24.04", LinuxHome = "/home/test user/.codex" };
    Directory.CreateDirectory(wsl.Home); File.WriteAllText(Path.Combine(wsl.Home, "config.toml"), "model='test'\n");
    var wr = Integration.Deploy(wsl, @"C:\Users\测试 用户\relay.exe", true);
    Check(wr.InstalledNotify.SequenceEqual(new[] { "python3", "/home/test user/.codex/codex-notifier/bridge.py", "--notify" }), "WSL callback preserves arguments and spaces");
    Check(File.Exists(Path.Combine(wsl.Home, "codex-notifier", "bridge.py")), "WSL bridge emitted");
    Integration.Remove(wsl); Check(File.ReadAllText(Path.Combine(wsl.Home, "config.toml")) == "model='test'\n", "WSL integration removed exactly");

    var tracker = new TurnTracker();
    TurnEvent Ev(string turn, string kind, bool child = false) => new("s", "t", turn, kind, DateTimeOffset.UtcNow, child);
    tracker.Apply(Ev("1", "started"), true); tracker.Apply(Ev("2", "started"), true);
    var first = tracker.Apply(Ev("1", "completed"), true);
    Check(first.Notify && first.Active == 1, "one completed conversation leaves another working");
    Check(!tracker.Apply(Ev("1", "completed"), true).Notify, "watcher and callback cannot notify twice");
    Check(!tracker.Apply(Ev("1", "started"), true).Changed, "late start cannot revive completed turn");
    Check(!tracker.Apply(Ev("2", "interrupted"), true).Notify && tracker.Active == 0, "interruption does not announce success");
    Check(!tracker.Apply(Ev("child", "completed", true), true).Notify, "subagent completion ignored");
    Check(!tracker.Apply(Ev("paused", "completed"), false).Notify, "paused notification suppressed");
    Check(!tracker.Apply(Ev("paused", "completed"), true).Notify, "reenabling does not replay muted completions");
    Check(!tracker.Apply(Ev("history", "completed"), true, true).Notify, "startup historical completion never plays");
    Check(!tracker.Apply(Ev("failed", "failed"), true).Notify, "failure does not announce success");
    Check(EventParser.ParseNotify("{\"type\":\"agent-turn-complete\",\"thread-id\":\"t\"}", "s") == null, "old notify without turn ID cannot bypass deduplication");
    string thread = ""; bool subagent = false;
    EventParser.ParseRollout("{\"type\":\"session_meta\",\"payload\":{\"id\":\"kid\",\"source\":{\"subagent\":{}}}}", "s", ref thread, ref subagent);
    Check(subagent && thread == "kid", "subagent identified from session metadata");

    string sessionDir = Path.Combine(temp, "watch", "sessions", DateTime.UtcNow.ToString("yyyy/MM/dd")); Directory.CreateDirectory(sessionDir);
    string rollout = Path.Combine(sessionDir, "rollout-test.jsonl");
    string Meta() => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "main", source = "vscode" } });
    string Record(string turn, string kind) => JsonSerializer.Serialize(new { type = "event_msg", timestamp = DateTimeOffset.UtcNow, payload = new { type = kind, turn_id = turn } });
    File.WriteAllText(rollout, Meta() + "\n" + Record("old", "task_started") + "\n" + Record("old", "task_complete") + "\n");
    var events = new ConcurrentQueue<(TurnEvent, bool)>();
    using (var monitor = new SessionMonitor([new SourceProfile { Id = "watch", Home = Path.Combine(temp, "watch") }], (e, replay) => events.Enqueue((e, replay)), _ => { }))
    {
        monitor.Start();
        for (int i = 0; i < 50 && events.Count < 2; i++) await Task.Delay(100);
        Check(events.Count == 2 && events.All(x => x.Item2), "monitor reconstructs existing history without notifications");
        string fresh = Record("new", "task_started");
        File.AppendAllText(rollout, fresh[..(fresh.Length / 2)]);
        await Task.Delay(800); Check(events.Count == 2, "partial JSON write does not emit false state");
        File.AppendAllText(rollout, fresh[(fresh.Length / 2)..] + "\n" + Record("new", "task_complete") + "\n");
        for (int i = 0; i < 40 && events.Count < 4; i++) await Task.Delay(100);
        Check(events.Count == 4 && events.Skip(2).All(x => !x.Item2), "new complete records produce live events once");
    }
    // Regression: Codex resumes old threads in their original date directory.
    string oldHome = Path.Combine(temp, "old-conversation");
    string oldDir = Path.Combine(oldHome, "sessions", "2020", "01", "02"); Directory.CreateDirectory(oldDir);
    string oldFile = Path.Combine(oldDir, "rollout-old.jsonl");
    File.WriteAllText(oldFile, Meta() + "\n" + Record("past", "task_started") + "\n" + Record("past", "task_complete") + "\n");
    var oldEvents = new ConcurrentQueue<(TurnEvent, bool)>();
    using (var oldMonitor = new SessionMonitor([new SourceProfile { Id = "old", Home = oldHome }], (e, r) => oldEvents.Enqueue((e, r)), _ => { }))
    {
        oldMonitor.Start();
        for (int i = 0; i < 50 && oldEvents.Count < 2; i++) await Task.Delay(100);
        Check(oldEvents.Count == 2 && oldEvents.All(x => x.Item2), "old creation-date directory discovered by recent modification time");
        File.AppendAllText(oldFile, Record("resumed", "task_started") + "\n" + Record("resumed", "task_complete") + "\n");
        for (int i = 0; i < 40 && oldEvents.Count < 4; i++) await Task.Delay(100);
        Check(oldEvents.Count == 4 && oldEvents.Skip(2).All(x => !x.Item2), "resumed old conversation emits live start and completion");
    }
    string dormantFile = Path.Combine(oldDir, "rollout-dormant.jsonl");
    File.WriteAllText(dormantFile, Meta() + "\n" + Record("historical", "task_complete") + "\n");
    File.SetLastWriteTimeUtc(dormantFile, DateTime.UtcNow.AddMonths(-1));
    var dormantEvents = new ConcurrentQueue<(TurnEvent, bool)>();
    using (var dormantMonitor = new SessionMonitor([new SourceProfile { Id = "dormant", Home = oldHome }], (e, r) => dormantEvents.Enqueue((e, r)), _ => { }))
    {
        dormantMonitor.Start(); await Task.Delay(900);
        Check(!dormantEvents.Any(x => x.Item1.TurnId == "historical"), "inactive old file not read at startup");
        File.AppendAllText(dormantFile, Record("awakened", "task_started") + "\n" + Record("awakened", "task_complete") + "\n");
        for (int i = 0; i < 70 && !dormantEvents.Any(x => x.Item1.TurnId == "awakened" && x.Item1.Kind == "completed"); i++) await Task.Delay(100);
        Check(dormantEvents.Any(x => x.Item1.TurnId == "historical" && x.Item2), "late-discovered old history is marked replay");
        Check(dormantEvents.Count(x => x.Item1.TurnId == "awakened" && !x.Item2) == 2, "previously dormant conversation discovered after resume");
    }
    using var ipcStop = new CancellationTokenSource(); string pipe = "CN-Test-" + Guid.NewGuid().ToString("N");
    var received = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    var listener = Ipc.Listen(m => received.TrySetResult(m), ipcStop.Token, pipe);
    Check(await Ipc.Send(new("ping"), 1500, pipe), "current-user named pipe accepts relay");
    Check((await received.Task.WaitAsync(TimeSpan.FromSeconds(2))).Command == "ping", "IPC envelope received correctly");
    ipcStop.Cancel(); await listener;
    Check(!await Ipc.Send(new("ping"), 100, "nonexistent-" + Guid.NewGuid()), "relay exits promptly if desktop is absent");
    int relayIndex = Array.IndexOf(args, "--relay");
    if (relayIndex >= 0)
    {
        string relayInput = args[relayIndex + 1];
        string relayDir = Path.Combine(temp, "relay"); Directory.CreateDirectory(relayDir);
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(relayInput)!)) File.Copy(file, Path.Combine(relayDir, Path.GetFileName(file)));
        string relayExe = Path.Combine(relayDir, "CodexNotifier.Relay.exe");
        async Task CheckRelay(bool wsl)
        {
            string name = "CN-RelayTest-" + Guid.NewGuid().ToString("N");
            using var token = new CancellationTokenSource();
            var tcs = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var listen = Ipc.Listen(m => tcs.TrySetResult(m), token.Token, name);
            var psi = new System.Diagnostics.ProcessStartInfo(wsl ? "wsl.exe" : relayExe) { UseShellExecute = false, CreateNoWindow = true };
            if (wsl)
            {
                psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(args[Array.IndexOf(args, "--wsl") + 1]); psi.ArgumentList.Add("--exec");
                psi.ArgumentList.Add("/mnt/" + char.ToLowerInvariant(relayExe[0]) + relayExe[2..].Replace('\\', '/'));
            }
            psi.ArgumentList.Add("--ping"); psi.ArgumentList.Add(name);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(proc.ExitCode == 0, wsl ? "WSL launches native Windows relay successfully" : "native Windows relay exits successfully");
            Check((await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3))).Command == "ping", wsl ? "WSL -> Windows EXE -> isolated named pipe delivered" : "native EXE -> isolated named pipe delivered");
            token.Cancel(); await listen;
        }
        await CheckRelay(false); if (Array.IndexOf(args, "--wsl") >= 0) await CheckRelay(true);
    }
    Console.WriteLine($"\nALL {passed} CHECKS PASSED");
}
finally { Directory.Delete(temp, true); }
