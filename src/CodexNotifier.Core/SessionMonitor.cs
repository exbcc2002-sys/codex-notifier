using System.Text;

namespace CodexNotifier.Core;

public sealed class SessionMonitor : IDisposable
{
    sealed class Cursor
    {
        public long Offset, Baseline;
        public string Pending = "", Thread = "";
        public bool Child;
        public Decoder Decoder = Encoding.UTF8.GetDecoder();
    }
    readonly List<SourceProfile> sources;
    readonly Dictionary<string, Cursor> files = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource stop = new();
    readonly Action<TurnEvent, bool> onEvent;
    readonly Action<string> onIssue;
    Task? task;
    DateTime nextDiscover = DateTime.MinValue;
    readonly Dictionary<string, SourceProfile> paths = new(StringComparer.OrdinalIgnoreCase);
    public SessionMonitor(IEnumerable<SourceProfile> sources, Action<TurnEvent, bool> onEvent, Action<string> onIssue)
    { this.sources = sources.Where(x => x.Enabled).ToList(); this.onEvent = onEvent; this.onIssue = onIssue; }
    public void Start() => task = Task.Run(Loop);
    async Task Loop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= nextDiscover)
                {
                    Discover(); nextDiscover = DateTime.UtcNow.AddSeconds(4);
                }
                foreach (var (path, source) in paths.ToArray())
                {
                    if (stop.IsCancellationRequested) break;
                    try { Read(path, source); }
                    catch (IOException) { /* transient writer/WSL availability; retry without announcing completion */ }
                    catch (UnauthorizedAccessException) { onIssue("无权读取某个会话文件，请检查所选配置目录。"); }
                }
                await Task.Delay(650, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { onIssue("监测停止：" + e.GetType().Name); }
    }
    readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    bool initial = true;
    int lastDiscoveredCount = -1;
    void Discover()
    {
        foreach (var source in sources)
        {
            string dir = Path.Combine(source.Home, "sessions");
            if (!Directory.Exists(dir)) continue;
            try
            {
                // The date directory is the conversation's creation date, not its last turn.
                // Reopened conversations can append into folders months old. Enumerate metadata
                // across the whole tree, then read only recently modified or already tracked files.
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string p in Directory.EnumerateFiles(dir, "*.jsonl", options))
                {
                    if (stop.IsCancellationRequested) return;
                    if (files.ContainsKey(p)) continue;
                    var info = new FileInfo(p);
                    if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-3)) continue;
                    paths[p] = source;
                    files[p] = new Cursor { Baseline = initial ? info.Length : 0 };
                }
            }
            catch (IOException) { onIssue("WSL 配置目录暂时不可用，等待恢复。"); }
            catch (UnauthorizedAccessException) { onIssue("配置目录不可访问。"); }
        }
        if (lastDiscoveredCount != files.Count)
        {
            lastDiscoveredCount = files.Count;
            onIssue($"已发现 {files.Count} 份近期更新的会话文件（包含旧日期目录）。");
        }
        initial = false;
    }
    void Read(string path, SourceProfile source)
    {
        var c = files[path];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < c.Offset) { c = new Cursor { Baseline = stream.Length }; files[path] = c; }
        stream.Position = c.Offset;
        byte[] bytes = new byte[65536]; char[] chars = new char[65536];
        // Preserve UTF-8 decoder and partial line across polls. Never process an unfinished JSON record.
        for (int chunk = 0; chunk < 32; chunk++)
        {
            int limit = c.Baseline > c.Offset ? (int)Math.Min(bytes.Length, c.Baseline - c.Offset) : bytes.Length;
            int n = stream.Read(bytes, 0, limit); if (n == 0) break;
            int count = c.Decoder.GetChars(bytes, 0, n, chars, 0, false);
            c.Offset += n;
            var text = c.Pending + new string(chars, 0, count);
            int start = 0;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n')
            {
                string line = text[start..i]; start = i + 1;
                if (!line.Contains("\"event_msg\"") && !line.Contains("\"session_meta\"")) continue;
                try
                {
                    var ev = EventParser.ParseRollout(line, source.Id, ref c.Thread, ref c.Child);
                    if (ev != null)
                    {
                        // First-load records are state reconstruction only; never replay historical sounds.
                        // A previously inactive old conversation may first be discovered after
                        // startup. Its pre-start history must still never produce sounds.
                        bool replay = c.Baseline > 0 || ev.At < startedAt;
                        onEvent(ev, replay);
                    }
                }
                catch (System.Text.Json.JsonException) { onIssue("遇到无法解析的会话记录，已跳过；不会据此推断完成。"); }
            }
            c.Pending = text[start..];
            if (c.Offset >= c.Baseline) c.Baseline = 0;
            if (c.Pending.Length > 8 * 1024 * 1024) { c.Pending = ""; onIssue("单条会话记录过大，已跳过。"); }
        }
    }
    public void Dispose() { stop.Cancel(); }
}
