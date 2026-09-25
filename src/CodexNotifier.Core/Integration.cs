using System.Text;
using System.Text.Json;

namespace CodexNotifier.Core;

public sealed class IntegrationRecord
{
    public int Version { get; set; } = 1;
    public string Owner { get; set; } = "CodexNotifier";
    public string SourceId { get; set; } = "";
    public string? OriginalStatement { get; set; }
    public string[] PreviousNotify { get; set; } = [];
    public string[] InstalledNotify { get; set; } = [];
    public bool ForwardPrevious { get; set; } = true;
    public string WindowsRelay { get; set; } = "";
    public string Distro { get; set; } = "";
    public string Status { get; set; } = "prepared";
}

public static class Integration
{
    public const string Folder = "codex-notifier";
    public static string RecordPath(SourceProfile s) => Path.Combine(s.Home, Folder, "integration.json");
    public static string Status(SourceProfile s)
    {
        if (!File.Exists(Path.Combine(s.Home, "config.toml"))) return "未找到 config.toml";
        var r = JsonStore.Read<IntegrationRecord>(RecordPath(s));
        if (r == null || r.Status == "removed") return "只读监测（未部署回调）";
        return NotifyConfig.Read(File.ReadAllText(Path.Combine(s.Home, "config.toml"))).SequenceEqual(r.InstalledNotify)
            ? "已部署 notify；监测开始／完成事件" : "配置已变化，需要人工复核";
    }
    public static IntegrationRecord Deploy(SourceProfile s, string relay, bool forwardPrevious)
    {
        string config = Path.Combine(s.Home, "config.toml");
        if (!File.Exists(config)) throw new FileNotFoundException("请先选择实际存在的 Codex 配置目录。", config);
        var text = File.ReadAllText(config);
        var previous = NotifyConfig.Read(text);
        var old = JsonStore.Read<IntegrationRecord>(RecordPath(s));
        if (old is { Owner: "CodexNotifier" } && old.Status != "removed")
        {
            if (!previous.SequenceEqual(old.InstalledNotify)) throw new InvalidOperationException("notify 已被其他程序修改。为保留你的配置，未继续部署。");
            old.ForwardPrevious = forwardPrevious;
            JsonStore.Write(RecordPath(s), old); return old;
        }
        if (previous.Any(x => x.Contains("CodexNotifier.Relay", StringComparison.OrdinalIgnoreCase) || x.Contains("codex-notifier/bridge.py")))
            throw new InvalidOperationException("检测到缺少部署记录的旧接入。请先恢复原 notify，避免嵌套转发。");
        string own = Path.Combine(s.Home, Folder); Directory.CreateDirectory(own);
        var record = new IntegrationRecord { SourceId = s.Id, OriginalStatement = NotifyConfig.Find(text)?.Raw, PreviousNotify = previous,
            WindowsRelay = relay, Distro = s.Distro, ForwardPrevious = forwardPrevious };
        if (s.IsWsl)
        {
            if (!s.LinuxHome.StartsWith('/')) throw new InvalidOperationException("WSL 配置需要 Linux 绝对路径。");
            File.WriteAllText(Path.Combine(own, "bridge.py"), PythonBridge, new UTF8Encoding(false));
            record.InstalledNotify = ["python3", s.LinuxHome.TrimEnd('/') + "/" + Folder + "/bridge.py", "--notify"];
        }
        else record.InstalledNotify = [relay, "--notify", "--record", RecordPath(s)];
        var updated = NotifyConfig.Set(text, record.InstalledNotify);
        // Durable recovery record precedes config commit. Only this entry is reverted on removal.
        JsonStore.Write(RecordPath(s), record);
        File.WriteAllText(Path.Combine(own, "config.before-install.toml"), text, new UTF8Encoding(false));
        AtomicCheckedWrite(config, text, updated);
        record.Status = "installed"; JsonStore.Write(RecordPath(s), record);
        return record;
    }
    public static void Remove(SourceProfile s)
    {
        var r = JsonStore.Read<IntegrationRecord>(RecordPath(s)) ?? throw new InvalidOperationException("没有本程序的部署记录。");
        if (r.Owner != "CodexNotifier") throw new InvalidOperationException("部署记录不属于本程序。");
        if (r.Status == "removed") return;
        string config = Path.Combine(s.Home, "config.toml"); var text = File.ReadAllText(config);
        if (!NotifyConfig.Read(text).SequenceEqual(r.InstalledNotify)) throw new InvalidOperationException("notify 已被其他程序修改，未覆盖。原回调保存在 integration.json 中。");
        AtomicCheckedWrite(config, text, NotifyConfig.Restore(text, r.OriginalStatement));
        r.Status = "removed"; JsonStore.Write(RecordPath(s), r);
        // Keep recovery metadata; there is no active hook or callback after removal.
    }
    static void AtomicCheckedWrite(string path, string expected, string next)
    {
        string tmp = path + ".codex-notifier." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, next, new UTF8Encoding(false));
            if (File.ReadAllText(path) != expected) throw new IOException("配置在部署过程中发生变化，已取消写入。");
            File.Move(tmp, path, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    public const string PythonBridge = """
#!/usr/bin/env python3
# CodexNotifier owned relay. No prompts or response text are logged.
import sys, json, pathlib, subprocess, base64

def main():
    record = json.loads(pathlib.Path(__file__).with_name('integration.json').read_text(encoding='utf-8-sig'))
    payload = sys.argv[-1] if len(sys.argv) > 1 else '{}'
    # Keep previous callbacks in their original Linux execution context.
    if record.get('ForwardPrevious') and record.get('PreviousNotify'):
        try:
            subprocess.Popen(record['PreviousNotify'] + [payload], stdin=subprocess.DEVNULL,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
        except OSError:
            pass
    try:
        p = json.loads(payload)
        # Forward only IDs, never the conversation text. Desktop verifies main-session identity.
        event = {'SourceId':record['SourceId'], 'ThreadId':p.get('thread-id',p.get('thread_id','')),
                 'TurnId':p.get('turn-id',p.get('turn_id','')), 'Kind':'completed'}
        if p.get('type') != 'agent-turn-complete' or not event['ThreadId'] or not event['TurnId']:
            return
        event['At'] = __import__('datetime').datetime.now(__import__('datetime').timezone.utc).isoformat()
        data = base64.b64encode(json.dumps(event).encode()).decode()
        exe = record['WindowsRelay']
        # Installed relay lives on the current Windows user's local drive.
        if len(exe) > 2 and exe[1] == ':':
            exe = '/mnt/' + exe[0].lower() + exe[2:].replace('\\','/')
        subprocess.run([exe, '--event', data], stdin=subprocess.DEVNULL,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=2)
    except (ValueError, OSError, subprocess.TimeoutExpired):
        pass

if __name__ == '__main__':
    try:
        main()
    except Exception:
        pass
""";
}
