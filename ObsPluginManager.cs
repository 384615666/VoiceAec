using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;

namespace VoiceAec;

internal sealed class ObsPluginManager
{
    private readonly object gate = new();
    private bool busy;
    private string message = "";
    private string? pendingAction;
    private bool? lastOk;
    private readonly ObsPluginInstaller installer = ObsPluginInstaller.ForApplication();

    public object Status()
    {
        lock (gate)
        {
            ObsPluginReceipt? receipt = null;
            string? error = null;
            try { receipt = installer.ReadReceipt(); }
            catch (Exception ex) { error = ex.Message; }
            var obsRunning = ObsPluginInstaller.IsObsRunning();
            // While a task runs the progress text is rebuilt for the current
            // language; the final receipt message uses the language it ended in.
            var currentMessage = busy && pendingAction != null
                ? pendingAction == "install"
                    ? UiText.S("正在安装到 OBS，系统可能请求管理员权限…",
                        "Installing into OBS; Windows may ask for administrator permission…")
                    : UiText.S("正在卸载 OBS 插件，系统可能请求管理员权限…",
                        "Uninstalling the OBS plugin; Windows may ask for administrator permission…")
                : message;
            return new
            {
                ok = true, busy, message = currentMessage, lastOk, obsRunning,
                installed = installer.Installed,
                managed = receipt != null,
                version = receipt?.Version,
                hadPrevious = receipt?.HadPrevious ?? false,
                payloadAvailable = installer.PayloadAvailable,
                canInstall = !busy && !obsRunning && error == null && installer.PayloadAvailable,
                canUninstall = !busy && !obsRunning && receipt != null && error == null,
                target = installer.Target, error
            };
        }
    }

    public bool Start(string action)
    {
        if (action is not ("install" or "uninstall"))
            throw new ArgumentException(UiText.S("未知的 OBS 插件操作。", "Unknown OBS plugin action."));
        lock (gate)
        {
            if (busy) return false;
            if (ObsPluginInstaller.IsObsRunning())
                throw new InvalidOperationException(UiText.S("请先退出 OBS 后重试。", "Quit OBS first, then try again."));
            if (action == "uninstall" && installer.ReadReceipt() == null)
                throw new InvalidOperationException(UiText.S("此插件不是由本程序安装的，不会自动删除。",
                    "This plugin was not installed by VoiceAec and will not be removed automatically."));
            if (action == "install" && !installer.PayloadAvailable)
                throw new InvalidOperationException(UiText.S("当前发布包缺少 OBS 插件资源，请使用完整整合包。",
                    "This release is missing the OBS plugin payload; use the complete package."));
            busy = true;
            pendingAction = action;
            message = action == "install"
                ? UiText.S("正在安装到 OBS，系统可能请求管理员权限…",
                    "Installing into OBS; Windows may ask for administrator permission…")
                : UiText.S("正在卸载 OBS 插件，系统可能请求管理员权限…",
                    "Uninstalling the OBS plugin; Windows may ask for administrator permission…");
            lastOk = null;
        }
        _ = Task.Run(async () =>
        {
            ObsPluginResult result;
            try
            {
                var id = Guid.NewGuid().ToString("N");
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = AppContext.BaseDirectory
                };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
                start.ArgumentList.Add("--obs-plugin-action");
                start.ArgumentList.Add(action);
                start.ArgumentList.Add("--obs-plugin-operation");
                start.ArgumentList.Add(id);
                using var process = Process.Start(start)
                    ?? throw new IOException(UiText.S("无法启动 OBS 插件安装程序。", "Could not start the OBS plugin installer."));
                await process.WaitForExitAsync();
                var output = ResultPath(id);
                if (File.Exists(output))
                {
                    result = JsonSerializer.Deserialize<ObsPluginResult>(File.ReadAllText(output))
                        ?? new(false, UiText.S("安装程序未返回有效结果。", "The installer did not return a valid result."));
                    try { File.Delete(output); } catch (UnauthorizedAccessException) { }
                }
                else result = new(false, UiText.S("安装程序未完成操作。",
                    "The installer did not finish.") + UiText.F("（退出码 {0}）", $" (exit code {0})", process.ExitCode));
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { result = new(false, UiText.S("已取消管理员授权，插件未作更改。", "Administrator permission was declined; the plugin was not changed.")); }
            catch (Exception ex) { result = new(false, ex.Message); }
            lock (gate)
            {
                message = result.Message;
                lastOk = result.Ok;
                busy = false;
                pendingAction = null;
            }
        });
        return true;
    }

    private static string ResultPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException(UiText.S("无效的操作编号。", "Invalid operation id."));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VoiceAec", "ObsPlugin", "results", id + ".json");
    }

    public static int RunHelper(string[] args)
    {
        // This path runs before desktop/audio initialization or the normal
        // single-instance guard. It accepts no caller-provided filesystem path.
        TraySupport.HideConsole();
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        var action = Value("--obs-plugin-action");
        var id = Value("--obs-plugin-operation");
        if (action is not ("install" or "uninstall") || !Guid.TryParseExact(id, "N", out _)) return 2;
        ObsPluginResult result;
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException(UiText.S("安装到 OBS 需要管理员权限。",
                    "Administrator permission is required to install into OBS."));
            using var mutex = new Mutex(false, @"Global\VoiceAec-ObsPlugin-Installer");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException(UiText.S("另一个 OBS 插件安装任务正在执行，请稍后重试。",
                    "Another OBS plugin task is running; try again later."));
                var installer = ObsPluginInstaller.ForApplication();
                result = action == "install" ? installer.Install() : installer.Uninstall();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception ex) { result = new(false, ex.Message); }
        try
        {
            var path = ResultPath(id!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(result));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        return result.Ok ? 0 : 1;
    }
}
