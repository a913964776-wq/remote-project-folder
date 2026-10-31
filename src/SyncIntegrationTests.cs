using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading.Tasks;
using System.Collections.Generic;

static class SyncIntegrationTests {
    static readonly List<string> passed = new List<string>();
    public static int Run(string[] args) { return RunAsync(args).GetAwaiter().GetResult(); }
    static async Task Wait(Func<Task<bool>> check, string label, int seconds) {
        for (int i = 0; i < seconds; i++) { if (await check()) { passed.Add("PASS: " + label); return; } await Task.Delay(1000); }
        throw new Exception("Timeout: " + label);
    }
    static Task<bool> Yes(bool value) { return Task.FromResult(value); }
    static async Task<int> RunAsync(string[] args) {
        string root = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.Combine(Path.GetTempPath(), "rpf-integration-" + Guid.NewGuid().ToString("N"));
        string report = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Program.BaseDir, "sync-test.txt");
        string exe = Path.Combine(Program.BaseDir, "vendor", "syncthing", "syncthing.exe");
        var a = new SyncEngine(exe, Path.Combine(root, "engine-a"), 28384, 22021);
        var b = new SyncEngine(exe, Path.Combine(root, "engine-b"), 28385, 22020);
        Exception failure = null;
        try {
            Directory.CreateDirectory(root); await a.Start(); await b.Start();
            var pa = new SyncProfile { Host = "127.0.0.1", User = "test", LocalRoot = Path.Combine(root, "files-a"), RemoteRoot = "/home/test/project", RemoteDevice = b.DeviceId };
            var pb = new SyncProfile { Host = "127.0.0.1", User = "test", LocalRoot = Path.Combine(root, "files-b"), RemoteRoot = "/home/test/project", RemoteDevice = a.DeviceId };
            await a.Configure(pa); await b.Configure(pb);
            // The production client initiates connections to the Linux listener.
            await b.Call("PATCH", "/rest/config/devices/" + a.DeviceId, new { addresses = new[] { "tcp://127.0.0.1:22021" } });
            File.WriteAllText(Path.Combine(pa.LocalRoot, "local.txt"), "local-to-server 中文"); await a.Scan(pa, "");
            await Wait(() => Yes(File.Exists(Path.Combine(pb.LocalRoot,"local.txt")) && File.ReadAllText(Path.Combine(pb.LocalRoot,"local.txt")) == "local-to-server 中文"), "local -> server", 90);
            File.WriteAllText(Path.Combine(pb.LocalRoot, "agent-result.md"), "Claude generated output"); await b.Scan(pb, "");
            await Wait(() => Yes(File.Exists(Path.Combine(pa.LocalRoot,"agent-result.md")) && File.ReadAllText(Path.Combine(pa.LocalRoot,"agent-result.md")) == "Claude generated output"), "server/agent result -> local", 60);
            Directory.CreateDirectory(Path.Combine(pa.LocalRoot, ".git")); File.WriteAllText(Path.Combine(pa.LocalRoot, ".git", "config"), "not for sync");
            File.WriteAllText(Path.Combine(pa.LocalRoot, ".env"), "DUMMY_TEST_ONLY=secret");
            File.WriteAllText(Path.Combine(pa.LocalRoot, "public.txt"), "sync fence"); await a.Scan(pa, "");
            await Wait(() => Yes(File.Exists(Path.Combine(pb.LocalRoot,"public.txt"))), "ignore test sync fence", 60);
            if (File.Exists(Path.Combine(pb.LocalRoot,".env")) || File.Exists(Path.Combine(pb.LocalRoot,".git","config"))) throw new Exception("Ignore rules leaked excluded test files"); passed.Add("PASS: .env/.git excluded");
            string shot;
            using (var img = new Bitmap(10, 10)) { img.SetPixel(1, 1, Color.Blue); shot = Workbench.SaveImage(img, pa.LocalRoot, root); }
            await a.Scan(pa, shot);
            await Wait(async () => await a.IsRemoteAvailable(pa, shot), "screenshot remote receipt confirmed by peer index", 60);
            using (var img = new Bitmap(Path.Combine(pb.LocalRoot,shot.Replace('/',Path.DirectorySeparatorChar)))) if (img.GetPixel(1,1).ToArgb() != Color.Blue.ToArgb()) throw new Exception("Screenshot bytes differ");
            passed.Add("PASS: screenshot PNG received intact");
            File.Delete(Path.Combine(pa.LocalRoot,"local.txt")); await a.Scan(pa, "");
            await Wait(() => Yes(!File.Exists(Path.Combine(pb.LocalRoot,"local.txt"))), "deletion propagated", 60);
            if (!Directory.Exists(Path.Combine(pb.LocalRoot,".stversions")) || !Directory.GetFiles(Path.Combine(pb.LocalRoot,".stversions"),"local*",SearchOption.AllDirectories).Any()) throw new Exception("Deleted file version not retained"); passed.Add("PASS: deleted remote version retained");
            File.WriteAllText(Path.Combine(pa.LocalRoot,"conflict.txt"),"baseline"); await a.Scan(pa, "");
            await Wait(() => Yes(File.Exists(Path.Combine(pb.LocalRoot,"conflict.txt"))), "conflict baseline shared", 60);
            await a.Call("PATCH", "/rest/config/devices/" + b.DeviceId, new { paused = true });
            await b.Call("PATCH", "/rest/config/devices/" + a.DeviceId, new { paused = true });
            File.WriteAllText(Path.Combine(pa.LocalRoot,"offline.txt"),"queued while disconnected");
            File.WriteAllText(Path.Combine(pa.LocalRoot,"conflict.txt"),"local edit"); File.WriteAllText(Path.Combine(pb.LocalRoot,"conflict.txt"),"remote edit");
            await a.Scan(pa, ""); await b.Scan(pb, "");
            await a.Call("PATCH", "/rest/config/devices/" + b.DeviceId, new { paused = false });
            await b.Call("PATCH", "/rest/config/devices/" + a.DeviceId, new { paused = false });
            await Wait(() => Yes(File.Exists(Path.Combine(pb.LocalRoot,"offline.txt"))), "offline edits resume on reconnect", 90);
            await Wait(() => Yes(Directory.GetFiles(pa.LocalRoot,"*.sync-conflict-*").Any()), "concurrent edits preserve conflict copy", 60);
            var versions = Directory.GetFiles(pa.LocalRoot,"conflict*").Select(File.ReadAllText).ToArray();
            if (!versions.Contains("local edit") || !versions.Contains("remote edit")) throw new Exception("Conflict lost one side"); passed.Add("PASS: both conflicting contents recoverable");
            await a.Pause(pa, true);
            File.WriteAllText(Path.Combine(pb.LocalRoot,"paused.txt"),"wait for resume"); await b.Scan(pb, ""); await Task.Delay(3000);
            if (File.Exists(Path.Combine(pa.LocalRoot,"paused.txt"))) throw new Exception("Paused folder still pulled new file");
            await a.Pause(pa, false);
            await Wait(() => Yes(File.Exists(Path.Combine(pa.LocalRoot,"paused.txt"))), "pause / resume", 60);
        } catch (Exception e) { failure = e; }
        foreach (var engine in new[] { a, b }) { try { await engine.Call("POST", "/rest/system/shutdown", null); } catch { } }
        File.WriteAllText(report, String.Join(Environment.NewLine, passed) + Environment.NewLine + (failure == null ? "ALL PASSED" : "FAIL: " + failure));
        return failure == null ? 0 : 1;
    }
}
