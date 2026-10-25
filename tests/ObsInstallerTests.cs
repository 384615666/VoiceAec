using System.Security.Cryptography;
using System.Text.Json;
using VoiceAec;

internal static class ObsInstallerTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }

    public static void Run()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "VoiceAec-installer-tests-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(sandbox, "app");
        var plugins = Path.Combine(sandbox, "plugins");
        var store = Path.Combine(sandbox, "store");
        Directory.CreateDirectory(Path.Combine(app, "obs-plugin"));
        var files = new List<ObsPluginFile>();
        foreach (var destination in new[] { "bin/64bit/obs-localvqe.dll", "bin/64bit/liblocalvqe.dll",
            "bin/64bit/ggml.dll", "bin/64bit/ggml-base.dll", "data/locale/en-US.ini",
            "data/localvqe-v1.3-4.8M-f32.gguf" })
        {
            var source = "obs-plugin/file-" + files.Count;
            var bytes = System.Text.Encoding.UTF8.GetBytes("new package " + destination);
            File.WriteAllBytes(Path.Combine(app, source), bytes);
            files.Add(new(source, destination, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        void Manifest(string version = "test-1", ObsPluginFile[]? entries = null) =>
            File.WriteAllText(Path.Combine(app, ObsPluginInstaller.ManifestPath), JsonSerializer.Serialize(
                new ObsPluginPayload(1, version, entries ?? files.ToArray())));
        Manifest();
        var running = false;
        var installer = new ObsPluginInstaller(app, plugins, store, () => running);
        var target = installer.Target;
        var plugin = Path.Combine(target, "bin/64bit/obs-localvqe.dll");
        var otherPlugin = Path.Combine(plugins, "unrelated", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPlugin)!);
        File.WriteAllText(otherPlugin, "unrelated plugin");
        try
        {
            Check(installer.Install().Ok && File.Exists(plugin) && installer.ReadReceipt() != null,
                "fresh plugin installation validates and records owned files");
            Check(installer.Uninstall().Ok && !File.Exists(plugin) && File.ReadAllText(otherPlugin) == "unrelated plugin",
                "fresh uninstall removes owned plugin and leaves other plugins untouched");

            Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
            File.WriteAllText(plugin, "original plugin");
            Directory.CreateDirectory(Path.Combine(target, "data"));
            File.WriteAllText(Path.Combine(target, "data", "user-settings.json"), "original settings");
            installer.Install();
            var first = installer.ReadReceipt()!;
            Check(first.HadPrevious && File.ReadAllText(plugin) != "original plugin", "legacy plugin backed up before installation");
            Manifest("test-2");
            installer.Install();
            Check(installer.ReadReceipt()!.Id == first.Id, "updates retain the original pre-install backup");
            File.WriteAllText(Path.Combine(target, "data", "later-model.gguf"), "user-added model");
            installer.Uninstall();
            Check(File.ReadAllText(plugin) == "original plugin" &&
                File.ReadAllText(Path.Combine(target, "data", "user-settings.json")) == "original settings" &&
                File.ReadAllText(Path.Combine(target, "data", "later-model.gguf")) == "user-added model",
                "uninstall restores original plugin and preserves user settings and later additions");

            installer.Install();
            File.WriteAllText(plugin, "external update");
            var modified = installer.Uninstall();
            Check(modified.PreservedFiles > 0 && File.ReadAllText(plugin) == "external update",
                "uninstall preserves externally modified owned files");
            try { installer.Uninstall(); throw new Exception("must refuse unmanaged uninstall"); }
            catch (InvalidOperationException) { Console.WriteLine("PASS: unmanaged plugins cannot be uninstalled automatically"); }

            running = true;
            try { installer.Install(); throw new Exception("must refuse installation while OBS runs"); }
            catch (InvalidOperationException) { Console.WriteLine("PASS: running OBS blocks changes"); }
            running = false;

            File.AppendAllText(Path.Combine(app, files[0].Source), "corruption");
            try { installer.Install(); throw new Exception("must reject damaged payload"); }
            catch (InvalidDataException)
            {
                Check(File.ReadAllText(plugin) == "external update", "damaged package rejected before modifying installed files");
            }
            var escape = files.ToArray();
            escape[0] = escape[0] with { Destination = "data/../../escape.dll" };
            Manifest(entries: escape);
            try { installer.ReadPayload(); throw new Exception("must reject traversal"); }
            catch (InvalidDataException) { Console.WriteLine("PASS: manifest path traversal rejected"); }

            var receiptPath = Path.Combine(target, ObsPluginInstaller.ReceiptName);
            File.WriteAllText(receiptPath, "{\"Schema\":1,\"Id\":\"../outside\",\"Version\":\"x\",\"Files\":{}}");
            try { installer.Uninstall(); throw new Exception("must reject damaged receipt"); }
            catch (InvalidDataException) { Check(File.ReadAllText(plugin) == "external update", "invalid receipt cannot redirect uninstall outside its directory"); }

            File.Delete(receiptPath);
            File.WriteAllText(Path.Combine(app, files[0].Source), "new package " + files[0].Destination);
            Manifest();
            var checks = 0;
            var raced = new ObsPluginInstaller(app, plugins, store, () => ++checks > 1);
            try { raced.Install(); throw new Exception("must cancel commit when OBS starts"); }
            catch (InvalidOperationException)
            {
                Check(File.ReadAllText(plugin) == "external update" && !File.Exists(receiptPath),
                    "OBS starting during preparation leaves the original installation unchanged");
            }
        }
        finally
        {
            // This path was created above solely for this test. Validate its
            // resolved location before recursive cleanup on Windows.
            var full = Path.GetFullPath(sandbox);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("VoiceAec-installer-tests-"))
                Directory.Delete(full, true);
        }
    }
}
