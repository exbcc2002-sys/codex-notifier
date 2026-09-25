using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace CodexNotifier.Core;

// The parser validates semantics. The lexical editor changes exactly one root statement,
// preserving all unrelated comments, whitespace, multiline strings and tables byte-for-byte.
public static class NotifyConfig
{
    public sealed record Entry(int Start, int End, string Raw);
    public static string[] Read(string text)
    {
        TomlTable model;
        try { model = Toml.ToModel(text); }
        catch { throw new InvalidDataException("config.toml 格式无效，未修改配置；请先在编辑器中检查语法。"); }
        if (!model.TryGetValue("notify", out var v)) return [];
        if (v is not TomlArray a || a.Any(x => x is not string)) throw new InvalidDataException("notify 必须是字符串数组，未修改配置。");
        return a.Cast<string>().ToArray();
    }
    public static Entry? Find(string text)
    {
        foreach (var (start, end) in Statements(text))
        {
            var s = text[start..end]; var trim = s.TrimStart();
            if (trim.StartsWith('[')) return null;
            if (Regex.IsMatch(s, "^\\s*(?:notify|\"notify\"|'notify')\\s*=")) return new(start, end, s);
        }
        return null;
    }
    public static string Set(string text, string[] command)
    {
        _ = Read(text);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        string statement = "notify = [" + string.Join(", ", command.Select(Quote)) + "]" + nl;
        var entry = Find(text);
        var result = entry is null ? statement + text : text[..entry.Start] + statement + text[entry.End..];
        _ = Read(result); return result;
    }
    public static string Restore(string text, string? originalStatement)
    {
        _ = Read(text); var e = Find(text) ?? throw new InvalidDataException("接入配置已经被修改，无法自动撤销。");
        var result = text[..e.Start] + (originalStatement ?? "") + text[e.End..];
        _ = Read(result); return result;
    }
    static string Quote(string value)
    {
        _ = new System.Text.UTF8Encoding(false, true).GetByteCount(value);
        var b = new System.Text.StringBuilder("\"");
        foreach (char c in value)
        {
            if (c == '\\') b.Append("\\\\");
            else if (c == '"') b.Append("\\\"");
            else if (c < 32 || c == 127) b.Append("\\u").Append(((int)c).ToString("X4"));
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }
    static IEnumerable<(int, int)> Statements(string text)
    {
        int start = 0, depth = 0; char quote = '\0'; bool triple = false, comment = false, escape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (comment) { if (c != '\n') continue; comment = false; }
            else if (quote != '\0')
            {
                if (escape) { escape = false; continue; }
                if (quote == '"' && c == '\\') { escape = true; continue; }
                if (c == quote)
                {
                    if (!triple) quote = '\0';
                    else if (i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote) { i += 2; quote = '\0'; triple = false; }
                }
                continue;
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                if (i + 2 < text.Length && text[i + 1] == c && text[i + 2] == c) { triple = true; i += 2; }
                continue;
            }
            else if (c == '#') { comment = true; continue; }
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
            if (c == '\n' && depth == 0) { yield return (start, i + 1); start = i + 1; }
        }
        if (start < text.Length) yield return (start, text.Length);
    }
}
