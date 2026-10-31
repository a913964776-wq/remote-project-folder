using System;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

static class WorkbenchTests {
    public static int Run(string[] args) {
        string temp = Path.Combine(Path.GetTempPath(), "rpf-test-" + Guid.NewGuid().ToString("N"));
        string output = args.Length > 1 ? args[1] : Path.Combine(Program.BaseDir, "workbench-test.txt");
        try {
            Directory.CreateDirectory(temp);
            var p = new SyncProfile { Host = "example.invalid", User = "test", RemoteRoot = "/home/test/中文 项目", LocalRoot = Path.Combine(temp, "项目") };
            SyncEngine.Validate(p, false);
            Assert(SyncEngine.RemotePath(p, "claude-inbox/中文.png") == "/home/test/中文 项目/claude-inbox/中文.png", "remote path");
            Assert(SyncEngine.ShellQuote("a'b") == "'a'\"'\"'b'", "shell escaping");
            bool blocked = false; try { SyncEngine.RemotePath(p, "../secret"); } catch { blocked = true; } Assert(blocked, "path traversal");
            blocked = false; var unsafeP = new SyncProfile { Host = p.Host, User = p.User, RemoteRoot = "/", LocalRoot = p.LocalRoot }; try { SyncEngine.Validate(unsafeP, false); } catch { blocked = true; } Assert(blocked, "system directory validation");
            using (var image = new Bitmap(12, 8)) {
                image.SetPixel(3, 4, Color.Red);
                string relative = Workbench.SaveImage(image, p.LocalRoot, temp);
                using (var result = new Bitmap(Path.Combine(p.LocalRoot, relative.Replace('/', Path.DirectorySeparatorChar)))) Assert(result.GetPixel(3,4).ToArgb() == Color.Red.ToArgb(), "PNG contents");
                string second = Workbench.SaveImage(image, p.LocalRoot, temp); Assert(relative != second, "unique screenshot names");
            }
            string atomic = Path.Combine(temp, "config.json"); Workbench.AtomicWrite(atomic, "old"); Workbench.AtomicWrite(atomic, "new"); Assert(File.ReadAllText(atomic) == "new", "atomic config replace");
            string source = Path.Combine(temp, "input.txt"); File.WriteAllText(source, "keep"); string target = Path.Combine(temp, "copy"); Directory.CreateDirectory(target); Workbench.CopyIn(source, target);
            File.WriteAllText(source, "overwrite"); blocked = false; try { Workbench.CopyIn(source, target); } catch { blocked = true; } Assert(blocked && File.ReadAllText(Path.Combine(target,"input.txt")) == "keep", "drag-drop does not overwrite");
            File.WriteAllText(output, "PASS: screenshot PNG/unique filenames, remote path, shell quoting, traversal/root guards, atomic configuration, drag-drop overwrite protection."); return 0;
        } catch (Exception e) { File.WriteAllText(output, "FAIL: " + e); return 1; }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }
    static void Assert(bool ok, string label) { if (!ok) throw new Exception(label); }
}
