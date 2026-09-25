using System.Diagnostics;
using CodexNotifier.Core;
namespace CodexNotifier.Desktop;

static class SourceDiscovery
{
    public static SourceProfile FromPath(string path, string distro)
    {
        path = path.Trim().Trim('"'); distro = distro.Trim();
        string linux = "";
        if (path.StartsWith('/'))
        {
            linux = path;
            if (path.StartsWith("/mnt/") && path.Length > 6 && path[6] == '/') path = char.ToUpperInvariant(path[5]) + ":\\" + path[7..].Replace('/', '\\');
            else if (distro != "") path = "\\\\wsl.localhost\\" + distro + path.Replace('/', '\\');
            else throw new InvalidOperationException("Linux 路径需要填写 WSL 发行版。");
        }
        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\');
        if (distro != "" && linux == "")
        {
            if (path.Length > 2 && path[1] == ':') linux = "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
            else
            {
                var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !parts[0].StartsWith("wsl", StringComparison.OrdinalIgnoreCase) || !parts[1].Equals(distro, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("WSL UNC 路径与发行版不匹配。");
                linux = "/" + string.Join('/', parts.Skip(2));
            }
        }
        return new() { Id = JsonStore.Hash(path.ToUpperInvariant()), Home = path, LinuxHome = linux, Distro = distro };
    }
    public static List<SourceProfile> Initial()
    {
        var results = new List<SourceProfile>();
        string? custom = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try { var s = FromPath(custom, custom.StartsWith('/') ? Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") ?? "Ubuntu24.04" : ""); if (Directory.Exists(s.Home)) results.Add(s); } catch { }
        }
        var normal = FromPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "");
        if (Directory.Exists(normal.Home) && results.All(x => x.Id != normal.Id)) results.Add(normal);
        return results;
    }
    public static async Task<List<SourceProfile>> DetectWsl()
    {
        var results = new List<SourceProfile>();
        string list = await Run("wsl.exe", ["--list", "--running", "--quiet"], true);
        foreach (var distro in list.Replace("\0", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().Trim('\uFEFF')))
        {
            if (distro == "") continue;
            try
            {
                var home = (await Run("wsl.exe", ["-d", distro, "--exec", "sh", "-lc", "printf '%s' \"${CODEX_HOME:-$HOME/.codex}\""])).Trim();
                if (home.StartsWith('/')) { var s = FromPath(home, distro); if (Directory.Exists(s.Home)) results.Add(s); }
            }
            catch { /* individual unavailable distro does not prevent other discovery */ }
        }
        return results;
    }
    public static async Task<string> Run(string exe, IEnumerable<string> args, bool unicode = false)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = unicode ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("无法启动 " + exe);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new IOException("环境检测超时。"); }
        var output = await stdout; await stderr;
        if (process.ExitCode != 0) throw new IOException("环境命令未成功执行：" + exe);
        return output;
    }
}
