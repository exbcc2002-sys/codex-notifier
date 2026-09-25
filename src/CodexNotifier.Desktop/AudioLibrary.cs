namespace CodexNotifier.Desktop;

internal static class AudioLibrary
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Audio");
    public static string DefaultPath => Path.Combine(DirectoryPath, "over.wav");

    public static string Resolve(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return DefaultPath;
        string full = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, stored));
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从程序 Audio 文件夹重新选择音频。");
        return full;
    }

    public static string Import(string source)
    {
        source = Path.GetFullPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("找不到所选音频。", source);
        if (new FileInfo(source).Length > 50 * 1024 * 1024) throw new InvalidOperationException("请选择小于 50 MB 的提示音文件。");
        if (!new[] { ".wav", ".mp3" }.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择 WAV 或 MP3 音频。");
        Directory.CreateDirectory(DirectoryPath);
        if (string.Equals(Path.GetDirectoryName(source), Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(AppContext.BaseDirectory, source);
        string target = Path.Combine(DirectoryPath, Path.GetFileName(source));
        if (File.Exists(target))
        {
            using var original = File.OpenRead(source);
            using var existing = File.OpenRead(target);
            if (System.Security.Cryptography.SHA256.HashData(original).SequenceEqual(System.Security.Cryptography.SHA256.HashData(existing)))
                return Path.GetRelativePath(AppContext.BaseDirectory, target);
            target = Path.Combine(DirectoryPath, Path.GetFileNameWithoutExtension(source) + "-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(source));
        }
        File.Copy(source, target, false);
        return Path.GetRelativePath(AppContext.BaseDirectory, target);
    }
}
