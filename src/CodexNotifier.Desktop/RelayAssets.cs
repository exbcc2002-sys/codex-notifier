namespace CodexNotifier.Desktop;

internal static class RelayAssets
{
    public static string Stage(string dataDirectory)
    {
        string destination = Path.Combine(dataDirectory, "relay");
        Directory.CreateDirectory(destination);
        string target = Path.Combine(destination, "CodexNotifier.Relay.exe");
        using var bundled = typeof(RelayAssets).Assembly.GetManifestResourceStream("CodexNotifier.BundledRelay.exe");
        if (bundled != null)
        {
            // Keep a stable callback path across upgrades and avoid replacing a running identical relay.
            var expected = System.Security.Cryptography.SHA256.HashData(bundled);
            if (File.Exists(target))
            {
                using var existing = File.OpenRead(target);
                if (System.Security.Cryptography.SHA256.HashData(existing).SequenceEqual(expected)) return target;
            }
            bundled.Position = 0;
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = File.Create(temporary)) bundled.CopyTo(output);
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return target;
        }
        string folder = Path.Combine(AppContext.BaseDirectory, "Bridge");
        if (!File.Exists(Path.Combine(folder, "CodexNotifier.Relay.exe")))
            throw new FileNotFoundException("未找到通知转发器。请使用完整开发构建目录，或重新下载单文件便携版。");
        foreach (string file in Directory.EnumerateFiles(folder)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        return target;
    }
}
