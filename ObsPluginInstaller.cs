using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace VoiceAec;

internal sealed record ObsPluginFile(string Source, string Destination, string Sha256);
internal sealed record ObsPluginPayload(int Schema, string Version, ObsPluginFile[] Files);
internal sealed record ObsPluginReceipt(int Schema, string Id, string Version, bool HadPrevious,
    Dictionary<string, string> Files);
internal sealed record ObsPluginResult(bool Ok, string Message, int PreservedFiles = 0);

// File transactions live separately from elevation and UI so the complete
// install/update/uninstall lifecycle can be tested in a disposable directory.
internal sealed class ObsPluginInstaller
{
    internal const string ReceiptName = ".voiceaec-install.json";
    internal const string ManifestPath = "obs-plugin/manifest.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string appRoot;
    private readonly string pluginsRoot;
    private readonly string storeRoot;
    private readonly Func<bool> obsRunning;
    public string Target => Path.Combine(pluginsRoot, "obs-localvqe");
    public bool Installed => File.Exists(Path.Combine(Target, "bin", "64bit", "obs-localvqe.dll"));
    public bool PayloadAvailable => File.Exists(Path.Combine(appRoot, ManifestPath));

    public ObsPluginInstaller(string appRoot, string pluginsRoot, string storeRoot, Func<bool> obsRunning)
    {
        this.appRoot = Path.GetFullPath(appRoot);
        this.pluginsRoot = Path.GetFullPath(pluginsRoot);
        this.storeRoot = Path.GetFullPath(storeRoot);
        this.obsRunning = obsRunning;
    }

    public static ObsPluginInstaller ForApplication() => new(AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "obs-studio", "plugins"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoiceAec", "ObsPlugin"),
        IsObsRunning);

    public static bool IsObsRunning()
    {
        foreach (var name in new[] { "obs64", "obs32", "obs" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) { if (!process.HasExited) return true; }
        return false;
    }

    public ObsPluginReceipt? ReadReceipt()
    {
        var path = Path.Combine(Target, ReceiptName);
        if (!File.Exists(path)) return null;
        RejectLinks(path);
        var receipt = JsonSerializer.Deserialize<ObsPluginReceipt>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("安装记录无法读取，已停止操作。");
        if (receipt.Schema != 1 || !Guid.TryParseExact(receipt.Id, "N", out _) || receipt.Files == null)
            throw new InvalidDataException("安装记录无效，已停止操作。");
        foreach (var item in receipt.Files)
        {
            _ = ChildPath(Target, item.Key);
            ValidateHash(item.Value);
        }
        return receipt;
    }

    public ObsPluginPayload ReadPayload()
    {
        var manifest = ChildPath(appRoot, ManifestPath);
        RejectLinks(manifest);
        var payload = JsonSerializer.Deserialize<ObsPluginPayload>(File.ReadAllText(manifest), Json)
            ?? throw new InvalidDataException("OBS 插件安装资源无效。");
        if (payload.Schema != 1 || string.IsNullOrWhiteSpace(payload.Version) || payload.Files is not { Length: > 0 })
            throw new InvalidDataException("OBS 插件安装资源无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in payload.Files)
        {
            var destination = file.Destination.Replace('\\', '/');
            if (!(destination.StartsWith("bin/64bit/", StringComparison.Ordinal) || destination.StartsWith("data/", StringComparison.Ordinal)) ||
                !names.Add(destination)) throw new InvalidDataException("插件清单包含重复或无效的目标文件。");
            _ = ChildPath(Target, file.Destination);
            var source = ChildPath(appRoot, file.Source);
            RejectLinks(source);
            ValidateHash(file.Sha256);
            if (!File.Exists(source) || !Hash(source).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"插件资源缺失或损坏：{file.Source}。请重新解压完整发布包。");
        }
        foreach (var required in new[] { "bin/64bit/obs-localvqe.dll", "bin/64bit/liblocalvqe.dll",
            "bin/64bit/ggml.dll", "bin/64bit/ggml-base.dll", "data/locale/en-US.ini",
            "data/localvqe-v1.3-4.8M-f32.gguf" })
            if (!names.Contains(required)) throw new InvalidDataException($"安装清单缺少 {required}。");
        return payload;
    }

    public ObsPluginResult Install()
    {
        EnsureStopped();
        var payload = ReadPayload(); // Validate all resources before touching an installation.
        RejectLinks(Target);
        RejectLinks(storeRoot);
        var old = ReadReceipt();
        if (old?.HadPrevious == true && !Directory.Exists(BackupPath(old.Id)))
            throw new InvalidDataException("原版本备份缺失，已停止更新。");
        var id = old?.Id ?? Guid.NewGuid().ToString("N");
        var hadPrevious = old?.HadPrevious ?? Directory.Exists(Target);
        var stage = Path.Combine(pluginsRoot, ".voiceaec-stage-" + Guid.NewGuid().ToString("N"));
        var newBackup = false;
        try
        {
            Directory.CreateDirectory(pluginsRoot);
            if (Directory.Exists(Target)) CopyTree(Target, stage);
            else Directory.CreateDirectory(stage);

            if (old == null && hadPrevious)
            {
                var backup = BackupPath(id);
                if (Directory.Exists(backup)) throw new IOException("备份目录已存在。");
                CopyTree(Target, backup);
                newBackup = true;
            }
            foreach (var file in payload.Files)
            {
                var destination = ChildPath(stage, file.Destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(ChildPath(appRoot, file.Source), destination, true);
                if (!Hash(destination).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("插件复制校验失败，原安装保持不变。");
            }
            // Retain ownership of an older version's files as well, so a later
            // uninstall can remove retired files instead of leaving DLLs behind.
            var files = old == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(old.Files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in payload.Files) files[file.Destination.Replace('\\', '/')] = file.Sha256;
            File.WriteAllText(Path.Combine(stage, ReceiptName), JsonSerializer.Serialize(
                new ObsPluginReceipt(1, id, payload.Version, hadPrevious, files), Json));
            EnsureStopped();
            Commit(stage);
            return new(true, hadPrevious ? "已安装到 OBS，并保留安装前备份。请重启 OBS 后添加 VoiceAec 音频滤镜。"
                : "已安装到 OBS。请重启 OBS 后，在麦克风滤镜中添加 VoiceAec。");
        }
        catch
        {
            if (newBackup) DeleteOwnedTree(BackupPath(id), storeRoot);
            throw;
        }
        finally { DeleteOwnedTree(stage, pluginsRoot); }
    }

    public ObsPluginResult Uninstall()
    {
        EnsureStopped();
        RejectLinks(Target);
        var receipt = ReadReceipt() ?? throw new InvalidOperationException("此插件不是由本程序安装的，不会自动删除。");
        var stage = Path.Combine(pluginsRoot, ".voiceaec-stage-" + Guid.NewGuid().ToString("N"));
        var preserved = 0;
        try
        {
            if (receipt.HadPrevious)
            {
                var backup = BackupPath(receipt.Id);
                if (!Directory.Exists(backup)) throw new IOException("安装前备份缺失，已停止卸载。");
                CopyTree(backup, stage);
            }
            else Directory.CreateDirectory(stage);
            var owned = new Dictionary<string, string>(receipt.Files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in TreeFiles(Target))
            {
                var relative = Path.GetRelativePath(Target, file).Replace('\\', '/');
                if (relative == ReceiptName) continue;
                // Leave later user files and externally modified files intact.
                if (owned.TryGetValue(relative, out var installedHash) &&
                    Hash(file).Equals(installedHash, StringComparison.OrdinalIgnoreCase)) continue;
                var destination = ChildPath(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, true);
                if (!owned.ContainsKey(relative) && receipt.HadPrevious &&
                    File.Exists(ChildPath(BackupPath(receipt.Id), relative))) continue;
                preserved++;
            }
            EnsureStopped();
            Commit(stage);
            if (Directory.Exists(Target) && !Directory.EnumerateFileSystemEntries(Target).Any()) Directory.Delete(Target);
            // Keep the original backup for recovery, even after a successful
            // uninstall. It is outside OBS's module discovery directories.
            var message = receipt.HadPrevious ? "已卸载本程序安装的版本，并恢复安装前的插件。请重新启动 OBS。"
                : "已卸载 OBS 插件。请重新启动 OBS。";
            if (preserved > 0) message += $" 已保留 {preserved} 个后来添加或修改的文件。";
            return new(true, message, preserved);
        }
        finally { DeleteOwnedTree(stage, pluginsRoot); }
    }

    private void Commit(string stage)
    {
        var rollback = Path.Combine(pluginsRoot, ".voiceaec-rollback-" + Guid.NewGuid().ToString("N"));
        var existed = Directory.Exists(Target);
        if (existed) Directory.Move(Target, rollback);
        try { Directory.Move(stage, Target); }
        catch
        {
            if (existed) Directory.Move(rollback, Target);
            throw;
        }
        // Backup and replacement are committed. A locked old file must not
        // turn success into an apparent failure or discard the original backup.
        try { DeleteOwnedTree(rollback, pluginsRoot); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void EnsureStopped()
    {
        if (obsRunning()) throw new InvalidOperationException("请先退出 OBS，再安装或卸载插件；完成后重新启动 OBS。");
    }
    private string BackupPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("无效的备份编号。");
        return ChildPath(storeRoot, $"backups/{id}/previous");
    }
    private static void ValidateHash(string value)
    {
        if (value == null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("无效的文件校验值。");
    }
    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal static string ChildPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('/', '\\').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("无效的插件文件路径。");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件超出安装目录。");
        return full;
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安装路径包含目录链接，请使用普通目录。");
    }
    private static IEnumerable<string> TreeFiles(string root)
    {
        RejectLinks(root);
        foreach (var item in Directory.EnumerateFileSystemEntries(root))
        {
            RejectLinks(item);
            if (Directory.Exists(item))
                foreach (var child in TreeFiles(item)) yield return child;
            else yield return item;
        }
    }
    private static void CopyTree(string source, string destination)
    {
        RejectLinks(destination);
        Directory.CreateDirectory(destination);
        foreach (var file in TreeFiles(source))
        {
            var target = ChildPath(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
    private static void DeleteOwnedTree(string path, string parent)
    {
        // Every recursive deletion resolves to a verified descendant of the
        // dedicated plugin/staging store. Never follow a directory junction.
        var full = ChildPath(parent, Path.GetRelativePath(parent, path));
        if (!Directory.Exists(full)) return;
        _ = TreeFiles(full).ToArray();
        Directory.Delete(full, true);
    }
}
