using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.Xml.Serialization;

[Serializable] public class Profile {
    public string Host = "";
    public string User = "";
    public int Port = 22;
    public string Root = "/home/";
    public string ProtectedPassword = "";
}

static class Program {
    public static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
    public static readonly string DataDir = Environment.GetEnvironmentVariable("RPF_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteProjectFolder");
    static readonly string ProfilePath = Path.Combine(DataDir, "profile.xml");
    static readonly string IniPath = Path.Combine(DataDir, "WinSCP.ini");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RemoteProjectFolder.v1");

    [STAThread] static int Main(string[] args) {
        if (args.Contains("--self-test")) return SelfTest(args);
        if (args.Contains("--test-workbench")) return WorkbenchTests.Run(args);
        if (args.Contains("--test-sync")) return SyncIntegrationTests.Run(args);
        if (args.Contains("--test-terminal")) return TerminalTests.Run(args);
        if (args.Contains("--render-preview")) {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var window = new Workbench(true)) {
                window.ShowInTaskbar = false; window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-30000, -30000);
                window.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(0, 0, window.Width, window.Height)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); }
                window.Close();
            }
            return 0;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (!args.Contains("--browse") && !args.Contains("--configure")) {
            bool first;
            string instance = BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(DataDir))).Replace("-", "").Substring(0, 16);
            using (var appMutex = new Mutex(true, "Local\\RemoteProjectWorkbench-" + Environment.UserName + "-" + instance, out first)) {
                if (!first) { MessageBox.Show("工作台已经打开，请切回现有窗口。"); return 0; }
                Directory.CreateDirectory(DataDir);
                Application.Run(new Workbench());
            }
            return 0;
        }
        bool owned;
        using (var mutex = new Mutex(true, "Local\\RemoteProjectFolder-" + Environment.UserName, out owned)) {
            if (!owned) { MessageBox.Show("项目窗口或配置窗口已打开。请先关闭它，再重新打开或修改配置。", "服务器项目文件夹"); return 0; }
            try {
                Directory.CreateDirectory(DataDir);
                Profile p = Load();
                if (p == null || args.Contains("--configure")) {
                    using (var form = new SetupForm(p ?? new Profile())) {
                        if (form.ShowDialog() != DialogResult.OK) return 0;
                        p = form.Result;
                    }
                    Save(p);
                    CreateShortcuts();
                }
                Validate(p);
                string exe = Path.Combine(BaseDir, "vendor", "winscp", "WinSCP.exe");
                if (!File.Exists(exe)) throw new Exception("缺少内置软件。请先完整解压 ZIP，再打开程序。");
                string password = Unprotect(p.ProtectedPassword);
                WriteIni(p, password, IniPath);
                password = null;
                try {
                    var info = new ProcessStartInfo(exe, "/ini=" + Quote(IniPath) + " Project /newinstance");
                    info.WorkingDirectory = Path.GetDirectoryName(exe);
                    using (var child = Process.Start(info)) { child.WaitForExit(); }
                } finally { ScrubPassword(IniPath); }
                return 0;
            } catch (Exception e) {
                MessageBox.Show(e.Message + "\n\n可打开“修改连接配置”重新设置。", "服务器项目文件夹", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
    public static string Protect(string text) {
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser));
    }
    public static string Unprotect(string text) {
        return String.IsNullOrEmpty(text) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), Entropy, DataProtectionScope.CurrentUser));
    }
    static Profile Load() {
        if (!File.Exists(ProfilePath)) return null;
        using (var f = File.OpenRead(ProfilePath)) return (Profile)new XmlSerializer(typeof(Profile)).Deserialize(f);
    }
    static void Save(Profile p) {
        string tmp = ProfilePath + ".tmp";
        using (var f = File.Create(tmp)) new XmlSerializer(typeof(Profile)).Serialize(f, p);
        if (File.Exists(ProfilePath)) File.Replace(tmp, ProfilePath, null); else File.Move(tmp, ProfilePath);
    }
    public static void Validate(Profile p) {
        if (String.IsNullOrWhiteSpace(p.Host) || p.Host.Any(c => Char.IsWhiteSpace(c) || "/\\\"@[]".Contains(c))) throw new Exception("请填写有效的服务器 IP 或主机名，不包含协议、端口或空格。");
        if (String.IsNullOrWhiteSpace(p.User) || p.User.Any(c => Char.IsControl(c))) throw new Exception("请填写有效的用户名。");
        if (p.Port < 1 || p.Port > 65535) throw new Exception("端口应为 1 到 65535。");
        if (String.IsNullOrEmpty(p.Root) || !p.Root.StartsWith("/") || p.Root.Any(c => Char.IsControl(c))) throw new Exception("项目目录必须是以 / 开头的服务器完整路径。");
    }
    public static string Quote(string s) { return "\"" + s.Replace("\"", "") + "\""; }
    // WinSCP's INI format escapes UTF-8 bytes and percent signs.
    static string IniValue(string text) {
        var b = new StringBuilder();
        foreach (byte c in Encoding.UTF8.GetBytes(text)) {
            if (c < 32 || c >= 127 || c == 37) b.Append("%" + c.ToString("X2")); else b.Append((char)c);
        }
        return b.ToString();
    }
    // Native WinSCP saved-session encoding. The persistent profile uses Windows DPAPI.
    static string NativePassword(string password, string user, string host) {
        byte[] data = Encoding.UTF8.GetBytes(user + host + password);
        using (var rng = RandomNumberGenerator.Create()) {
            byte[] raw = new byte[Math.Max(50, data.Length + 6)]; rng.GetBytes(raw);
            int start;
            raw[0] = 255;
            if (data.Length <= 255) { raw[1] = 0; raw[2] = (byte)data.Length; raw[3] = 0; start = 4; }
            else { raw[1] = 2; raw[2] = (byte)(data.Length >> 8); raw[3] = (byte)data.Length; raw[4] = 0; start = 5; }
            Buffer.BlockCopy(data, 0, raw, start, data.Length);
            return String.Concat(raw.Select(c => ((~c ^ 0xA3) & 255).ToString("X2")));
        }
    }
    public static void WriteIni(Profile p, string password, string path) {
        var text = new StringBuilder();
        // Preserve only trusted host keys between connections, not stale sites or passwords.
        if (File.Exists(path)) {
            bool take = false;
            foreach (string line in File.ReadAllLines(path)) {
                if (line.StartsWith("[")) take = line.Equals("[SshHostKeys]", StringComparison.OrdinalIgnoreCase);
                if (take) text.AppendLine(line);
            }
        }
        text.AppendLine("[Configuration\\Interface]");
        text.AppendLine("Interface=1\nLocaleSafe=2052\nCopyOnDoubleClick=0\nConfirmTransferring=0\nConfirmDeleting=1\nConfirmOverwriting=1\nDDDisableMove=1\nDDFakeFile=0");
        text.AppendLine("RandomSeedFile=" + IniValue(Path.Combine(DataDir, "winscp.rnd")));
        text.AppendLine("[Configuration\\Interface\\Explorer]\nDriveView=1\nDriveViewWidth=240\nShowFullAddress=1");
        text.AppendLine("[Sessions\\Project]");
        text.AppendLine("HostName=" + IniValue(p.Host));
        text.AppendLine("UserName=" + IniValue(p.User));
        text.AppendLine("PortNumber=" + p.Port);
        text.AppendLine("FSProtocol=2\nUpdateDirectories=0\nPingType=1\nPingIntervalSecs=30");
        text.AppendLine("RemoteDirectory=" + IniValue(p.Root));
        if (password.Length > 0) text.AppendLine("Password=" + NativePassword(password, p.User, p.Host));
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }
    static void ScrubPassword(string path) {
        if (!File.Exists(path)) return;
        File.WriteAllLines(path, File.ReadAllLines(path).Where(l => !l.StartsWith("Password=", StringComparison.OrdinalIgnoreCase)), new UTF8Encoding(false));
    }
    static void CreateShortcuts() {
        try {
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(type);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string target = Application.ExecutablePath;
            foreach (bool settings in new[] { false, true }) {
                dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, settings ? "服务器项目-修改配置.lnk" : "服务器项目文件夹.lnk"));
                shortcut.TargetPath = target; shortcut.Arguments = settings ? "--configure" : "";
                shortcut.WorkingDirectory = BaseDir; shortcut.IconLocation = Path.Combine(BaseDir, "vendor", "winscp", "WinSCP.exe") + ",0";
                shortcut.Save();
            }
        } catch { MessageBox.Show("配置已保存，但桌面快捷方式创建失败。仍可直接打开解压文件夹里的程序。", "提示"); }
    }
    static int SelfTest(string[] args) {
        try {
            if (Unprotect(Protect("test-中文-123")) != "test-中文-123") throw new Exception("DPAPI round trip");
            var p = new Profile { Host = "example.invalid", User = "test", Root = "/home/test/中文 项目%" };
            Validate(p);
            bool rejected = false;
            try { Validate(new Profile { Host = "bad host", User = "test", Root = "/" }); } catch { rejected = true; }
            if (!rejected) throw new Exception("Host validation");
            string temp = Path.Combine(Path.GetTempPath(), "RemoteProject-test-" + Guid.NewGuid() + ".ini");
            try {
                WriteIni(p, "test-only", temp);
                string ini = File.ReadAllText(temp);
                if (ini.Contains("test-only") || !ini.Contains("%E4%B8%AD") || !ini.Contains("%25")) throw new Exception("INI encoding");
                ScrubPassword(temp);
                if (File.ReadAllText(temp).Contains("Password=")) throw new Exception("Credential cleanup");
            } finally { if (File.Exists(temp)) File.Delete(temp); }
            File.WriteAllText(args.Length > 1 ? args[1] : Path.Combine(BaseDir, "self-test.txt"), "PASS: DPAPI, profile validation, UTF-8 INI, credential cleanup.");
            return 0;
        } catch (Exception e) { File.WriteAllText(Path.Combine(BaseDir, "self-test-error.txt"), e.Message); return 1; }
    }
}

sealed class SetupForm : Form {
    TextBox host = new TextBox(), user = new TextBox(), root = new TextBox(), password = new TextBox();
    NumericUpDown port = new NumericUpDown();
    CheckBox remember = new CheckBox();
    Profile original;
    public Profile Result;
    public SetupForm(Profile p) {
        original = p;
        Text = "服务器项目文件夹 · 连接配置"; ClientSize = new Size(540, 405);
        Font = new Font("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        var intro = new Label { Text = "配置一次，以后双击桌面图标直接打开项目。", Left = 24, Top = 20, Width = 495, Height = 30 }; Controls.Add(intro);
        AddRow("服务器 IP / 主机名", host, 65); AddRow("连接端口", port, 110);
        AddRow("用户名", user, 155); AddRow("登录密码", password, 200); AddRow("项目根目录", root, 245);
        host.Text = p.Host; user.Text = p.User; root.Text = p.Root;
        port.Minimum = 1; port.Maximum = 65535; port.Value = p.Port;
        password.UseSystemPasswordChar = true;
        remember.Text = "在这台电脑记住密码（仅当前 Windows 用户可解密）";
        remember.SetBounds(25, 292, 490, 28); remember.Checked = true; Controls.Add(remember);
        remember.CheckedChanged += delegate { password.Enabled = remember.Checked; };
        var note = new Label { Text = String.IsNullOrEmpty(p.ProtectedPassword) ? "不记住密码时，每次连接在软件中输入。" : "密码留空将保留已保存密码；取消勾选可清除。", Left = 25, Top = 324, Width = 490, Height = 24 }; Controls.Add(note);
        var save = new Button { Text = "保存并打开", Left = 355, Top = 360, Width = 155, Height = 32 };
        save.Click += delegate {
            try {
                Result = new Profile { Host = host.Text.Trim(), User = user.Text.Trim(), Port = (int)port.Value, Root = root.Text.Trim() };
                Program.Validate(Result);
                if (remember.Checked) {
                    if (password.Text.Length > 0) Result.ProtectedPassword = Program.Protect(password.Text);
                    else if (Result.Host == original.Host && Result.User == original.User && Result.Port == original.Port) Result.ProtectedPassword = original.ProtectedPassword;
                }
                DialogResult = DialogResult.OK; Close();
            } catch (Exception e) { MessageBox.Show(e.Message, "请检查配置"); }
        };
        Controls.Add(save); AcceptButton = save;
    }
    void AddRow(string label, Control input, int y) {
        Controls.Add(new Label { Text = label, Left = 25, Top = y + 3, Width = 160, Height = 28 });
        input.SetBounds(190, y, 320, 30); Controls.Add(input);
    }
}
