using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed class Workbench : Form {
    readonly string profileFile = Path.Combine(Program.DataDir, "sync-profile.json");
    readonly string queueFile = Path.Combine(Program.DataDir, "pending-attachments.json");
    readonly TextBox address = new TextBox { ReadOnly = true, Dock = DockStyle.Top };
    readonly ListView files = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, AllowDrop = true };
    readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label attachment = new Label { AutoSize = false, Dock = DockStyle.Fill, Text = "截图：复制图片后，点击粘贴截图，或在此窗口按 Ctrl+V。", TextAlign = ContentAlignment.MiddleLeft };
    readonly Panel terminalPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 22, 29) };
    readonly Label terminalHint = new Label { Dock = DockStyle.Fill, ForeColor = Color.WhiteSmoke, TextAlign = ContentAlignment.MiddleCenter, Text = "SSH · Claude Code\n\n先配置项目和同步，再点击「连接 Claude」。\n首次指纹确认和登录密码在终端中完成。\n密码不会由工作台保存。" };
    readonly Button pause = new Button { Text = "暂停同步", AutoSize = true };
    readonly NotifyIcon tray = new NotifyIcon { Text = "远程项目工作台", Icon = SystemIcons.Application, Visible = true };
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 5000 };
    readonly SyncEngine engine;
    readonly NativeTerminal terminal;
    readonly List<AttachmentItem> pending = new List<AttachmentItem>();
    SyncProfile profile;
    string currentDirectory;
    bool ready, busy, copying;
    FileSystemWatcher watcher;
    DateTime lastListRefresh = DateTime.MinValue;
    public Workbench(bool preview = false) {
        Text = "远程项目工作台 1.1 · 文件 + Claude";
        Width = 1280; Height = 820; MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 9);
        BackColor = Color.FromArgb(246, 248, 251);
        tray.Visible = !preview;
        status.Text = "先配置项目；服务器配对后启用同步。";
        profile = File.Exists(profileFile) ? SyncEngine.Json.Deserialize<SyncProfile>(File.ReadAllText(profileFile)) : new SyncProfile();
        try { if (File.Exists(queueFile)) pending.AddRange(SyncEngine.Json.Deserialize<List<AttachmentItem>>(File.ReadAllText(queueFile))); } catch { }
        engine = new SyncEngine(Path.Combine(Program.BaseDir, "vendor", "syncthing", "syncthing.exe"), Path.Combine(Program.DataDir, "sync-engine"), 18484, 22121);
        terminal = new NativeTerminal(terminalPanel);
        terminalPanel.Controls.Add(terminalHint);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8), WrapContents = false };
        AddButton(toolbar, "配置项目", Configure);
        AddButton(toolbar, "① 导出服务器配置", ExportServer);
        AddButton(toolbar, "② 启用同步", EnableSync);
        pause.Click += async delegate { await Guard(async () => {
            if (!ready) throw new Exception("同步引擎尚未就绪。");
            bool next = pause.Text == "暂停同步"; await engine.Pause(profile, next); profile.Enabled = !next; Save(); pause.Text = next ? "恢复同步" : "暂停同步";
        }); }; toolbar.Controls.Add(pause);
        AddButton(toolbar, "连接 Claude", ConnectTerminal);
        AddButton(toolbar, "SSH 命令行", async () => { RequireProfile(); terminalHint.Visible = false; try { await terminal.Connect(profile, false); } catch { terminalHint.Visible = true; throw; } });
        AddButton(toolbar, "断开终端", async () => { terminal.Dispose(); terminalHint.Visible = true; await Task.FromResult(0); });
        AddButton(toolbar, "打开本机目录", async () => { RequireProfile(); Process.Start("explorer.exe", Program.Quote(profile.LocalRoot)); await Task.FromResult(0); });
        AddButton(toolbar, "高级 SFTP", async () => { Process.Start(Application.ExecutablePath, "--browse"); await Task.FromResult(0); });
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1 };
        split.Size = new Size(1200, 600); split.SplitterDistance = 420;
        var fileHead = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 35 };
        AddButton(fileHead, "上一级", async () => { NavigateUp(); await Task.FromResult(0); });
        AddButton(fileHead, "刷新", async () => { RefreshFiles(); if (ready && profile.Enabled) await engine.Scan(profile, ""); });
        AddButton(fileHead, "新建文件夹", async () => { NewFolder(); await Task.FromResult(0); });
        files.Columns.Add("文件 / 文件夹", 240); files.Columns.Add("大小", 75); files.Columns.Add("修改时间", 140);
        files.DoubleClick += delegate { OpenSelected(); };
        files.ItemDrag += delegate { var selected = files.SelectedItems.Cast<ListViewItem>().Select(x => (string)x.Tag).ToArray(); if (selected.Length > 0) files.DoDragDrop(new DataObject(DataFormats.FileDrop, selected), DragDropEffects.Copy); };
        files.DragEnter += delegate(object s, DragEventArgs e) { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
        files.DragDrop += async delegate(object s, DragEventArgs e) { await Guard(async () => {
            RequireProfile(); if (copying) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths == null) return;
            string dest = currentDirectory ?? profile.LocalRoot;
            foreach (string source in paths) { string target = Path.Combine(dest, Path.GetFileName(source)); if (File.Exists(target) || Directory.Exists(target)) throw new Exception("目标已存在，请先重命名，避免覆盖：" + target); }
            copying = true;
            try { status.Text = "正在复制拖入的文件…"; await Task.Run(() => { foreach (string source in paths) CopyIn(source, dest); }); }
            finally { copying = false; RefreshFiles(); }
            if (ready && profile.Enabled) await engine.Scan(profile, "");
        }); };
        split.Panel1.Controls.Add(files); split.Panel1.Controls.Add(address); split.Panel1.Controls.Add(fileHead);
        split.Panel2.Controls.Add(terminalPanel);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 108, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var clipButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        AddButton(clipButtons, "粘贴截图 Ctrl+V", PasteScreenshot);
        AddButton(clipButtons, "复制图片远端路径", CopyAttachment);
        AddButton(clipButtons, "插入 Claude 输入框", InsertAttachment);
        AddButton(clipButtons, "同步高级管理", async () => { if (!ready) throw new Exception("引擎尚未就绪。"); Process.Start(engine.BaseUrl); await Task.FromResult(0); });
        bottom.Controls.Add(clipButtons, 0, 0); bottom.Controls.Add(attachment, 0, 1); bottom.Controls.Add(status, 0, 2);
        Controls.Add(split); Controls.Add(bottom); Controls.Add(toolbar);
        var menu = new ContextMenuStrip(); menu.Items.Add("打开工作台", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); }); menu.Items.Add("退出（同步引擎继续运行）", null, delegate { Close(); });
        tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
        Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
        Shown += async delegate { if (preview) return; await Guard(async () => {
            status.Text = "正在启动同步引擎…"; await engine.Start(); ready = true;
            if (profile.Enabled) { await engine.Configure(profile); Watch(); }
            status.Text = profile.Enabled ? "同步引擎就绪，正在检查服务器连接…" : "先配置项目，再导出服务器配置；两端配对后启用同步。";
            pause.Text = profile.Enabled ? "暂停同步" : "恢复同步";
            timer.Start(); RefreshFiles();
        }); };
        timer.Tick += async delegate { if (!busy && ready) { busy = true; try { await Poll(); } catch (Exception e) { status.Text = "同步检查失败：" + e.Message; } finally { busy = false; } } };
        FormClosed += delegate { timer.Stop(); if (watcher != null) watcher.Dispose(); terminal.Dispose(); tray.Dispose(); };
        KeyPreview = true;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
        if (keyData == (Keys.Control | Keys.V) && Clipboard.ContainsImage()) { var ignored = Guard(PasteScreenshot); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    void AddButton(Control parent, string text, Func<Task> action) {
        var b = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(3, 0, 3, 0) };
        b.Click += async delegate { b.Enabled = false; try { await Guard(action); } finally { if (!b.IsDisposed) b.Enabled = true; } }; parent.Controls.Add(b);
    }
    async Task Guard(Func<Task> action) { try { await action(); } catch (Exception e) { if (!IsDisposed) { status.Text = e.Message; MessageBox.Show(this, e.Message, "远程项目工作台", MessageBoxButtons.OK, MessageBoxIcon.Information); } } }
    void RequireProfile() { SyncEngine.Validate(profile, false); }
    void Save() { AtomicWrite(profileFile, SyncEngine.Json.Serialize(profile)); }
    public static void AtomicWrite(string path, string text) { string temp = path + ".tmp"; File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
    async Task Configure() {
        using (var form = new SyncSetup(profile)) {
            if (form.ShowDialog(this) != DialogResult.OK) return;
            if (profile.Enabled) {
                bool changed = profile.Host != form.Result.Host || profile.User != form.Result.User || profile.RemoteRoot != form.Result.RemoteRoot || profile.LocalRoot != form.Result.LocalRoot || profile.RemoteDevice != form.Result.RemoteDevice;
                if (changed) { await engine.Pause(profile, true); form.Result.Enabled = false; }
            }
            profile = form.Result; Save(); currentDirectory = null; Watch(); RefreshFiles();
        }
    }
    async Task ExportServer() {
        RequireProfile(); if (!ready) throw new Exception("请等待同步引擎启动。");
        string template = Path.Combine(Program.BaseDir, "server", "install.sh");
        using (var dialog = new SaveFileDialog { FileName = "server-setup-" + profile.User + ".sh", Filter = "Linux 配置脚本|*.sh" }) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dialog.FileName, engine.ExportServerScript(profile, File.ReadAllText(template)), new System.Text.UTF8Encoding(false));
            MessageBox.Show(this, "脚本已生成。通过高级 SFTP 上传到服务器，然后由 root 执行 bash 脚本文件名。\n脚本会安装专用后台服务，输出服务器设备 ID。\n在云防火墙放行 TCP 22020；不要开放管理端口 18385。\n将设备 ID 填回项目配置后，点击启用同步。", "服务器配对");
        }
        await Task.FromResult(0);
    }
    async Task EnableSync() {
        if (!ready) throw new Exception("请等待同步引擎启动。"); SyncEngine.Validate(profile, true);
        if (MessageBox.Show(this, "将启用本机与服务器的双向同步。两端已有文件会合并；修改和删除也会传播。\n请先备份重要文件，不要两边同时编辑同一文件。冲突会保留副本；收到的旧版本保留 20 份。\n确认同步这两个专用目录？\n\n" + profile.LocalRoot + "\n↕\n" + profile.RemoteRoot, "启用项目同步", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        await engine.Configure(profile); profile.Enabled = true; Save(); Watch(); RefreshFiles(); pause.Text = "暂停同步";
    }
    async Task ConnectTerminal() {
        RequireProfile(); terminalHint.Visible = false;
        try { await terminal.Connect(profile); } catch { terminalHint.Visible = true; throw; }
    }
    void Watch() {
        if (watcher != null) { watcher.Dispose(); watcher = null; }
        if (!Directory.Exists(profile.LocalRoot)) return;
        watcher = new FileSystemWatcher(profile.LocalRoot) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
        FileSystemEventHandler changed = delegate { lastListRefresh = DateTime.MinValue; };
        watcher.Created += changed; watcher.Changed += changed; watcher.Deleted += changed; watcher.Renamed += delegate { lastListRefresh = DateTime.MinValue; }; watcher.EnableRaisingEvents = true;
    }
    async Task Poll() {
        if (DateTime.UtcNow - lastListRefresh > TimeSpan.FromSeconds(15)) RefreshFiles();
        if (!profile.Enabled) { status.Text = "同步未启用或已暂停。引擎保持待命。"; return; }
        var connections = SyncEngine.Obj(SyncEngine.Obj(await engine.Call("GET", "/rest/system/connections", null))["connections"]);
        bool connected = connections.ContainsKey(profile.RemoteDevice) && (bool)SyncEngine.Obj(connections[profile.RemoteDevice])["connected"];
        var db = SyncEngine.Obj(await engine.Call("GET", "/rest/db/status?folder=" + Uri.EscapeDataString(profile.FolderId), null));
        var completion = SyncEngine.Obj(await engine.Call("GET", "/rest/db/completion?folder=" + Uri.EscapeDataString(profile.FolderId) + "&device=" + Uri.EscapeDataString(profile.RemoteDevice), null));
        int localNeed = Convert.ToInt32(db["needTotalItems"]);
        int remoteNeed = Convert.ToInt32(completion["needItems"]);
        int conflicts = CountConflicts(profile.LocalRoot);
        int errors = Convert.ToInt32(db["pullErrors"]);
        status.Text = connected ? ("服务器在线 · 本机待接收 " + localNeed + " · 远端待接收 " + remoteNeed + " · 状态 " + db["state"] + " · 错误 " + errors + " · 冲突 " + conflicts) : "服务器离线 / 未连接：本地修改已保留，连接恢复后继续同步。请检查 TCP 22020 和服务器服务。";
        foreach (var item in pending.Where(x => !x.Ready && x.FolderId == profile.FolderId && x.Device == profile.RemoteDevice).ToArray()) {
            if (!connected) break;
            if (await engine.IsRemoteAvailable(profile, item.Relative)) { item.Ready = true; PersistQueue(); }
        }
        var last = pending.LastOrDefault(x => x.FolderId == profile.FolderId && x.Device == profile.RemoteDevice);
        if (last != null) attachment.Text = last.Ready ? "截图已到服务器：" + last.RemotePath : "截图已保存，等待服务器接收：" + last.Relative;
    }
    static int CountConflicts(string root) {
        int n = 0;
        try { foreach (string file in Directory.GetFiles(root, "*.sync-conflict-*", SearchOption.TopDirectoryOnly)) n++; foreach (string dir in Directory.GetDirectories(root)) { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0 || Path.GetFileName(dir).StartsWith(".")) continue; n += CountConflicts(dir); } } catch { }
        return n;
    }
    void RefreshFiles() {
        lastListRefresh = DateTime.UtcNow;
        if (!Directory.Exists(profile.LocalRoot)) return;
        if (String.IsNullOrEmpty(currentDirectory) || !Directory.Exists(currentDirectory)) currentDirectory = profile.LocalRoot;
        address.Text = currentDirectory; files.BeginUpdate(); files.Items.Clear();
        try {
            foreach (string path in Directory.GetDirectories(currentDirectory).Concat(Directory.GetFiles(currentDirectory)).OrderBy(x => !Directory.Exists(x)).ThenBy(x => Path.GetFileName(x))) {
                string name = Path.GetFileName(path); if (name == ".stfolder" || name == ".stversions" || name.StartsWith(".syncthing.")) continue;
                bool dir = Directory.Exists(path); var item = new ListViewItem((dir ? "[目录] " : "") + name) { Tag = path };
                item.SubItems.Add(dir ? "" : new FileInfo(path).Length.ToString("N0")); item.SubItems.Add(File.GetLastWriteTime(path).ToString("MM-dd HH:mm:ss"));
                if (name.Contains(".sync-conflict-")) item.ForeColor = Color.DarkRed;
                files.Items.Add(item);
            }
        } catch (Exception e) { status.Text = "读取目录失败：" + e.Message; } finally { files.EndUpdate(); }
    }
    void OpenSelected() {
        if (files.SelectedItems.Count != 1) return; string path = (string)files.SelectedItems[0].Tag;
        if (Directory.Exists(path)) {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { MessageBox.Show("不在工作台内进入符号链接或目录联接。"); return; }
            currentDirectory = path; RefreshFiles();
        } else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    void NavigateUp() {
        if (String.IsNullOrEmpty(currentDirectory) || String.Equals(Path.GetFullPath(currentDirectory).TrimEnd('\\'), Path.GetFullPath(profile.LocalRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        currentDirectory = Directory.GetParent(currentDirectory).FullName; RefreshFiles();
    }
    void NewFolder() {
        RequireProfile(); string root = currentDirectory ?? profile.LocalRoot; string path = Path.Combine(root, "新建文件夹"); int i = 1; while (Directory.Exists(path) || File.Exists(path)) path = Path.Combine(root, "新建文件夹 " + i++); Directory.CreateDirectory(path); RefreshFiles();
    }
    public static void CopyIn(string source, string destination) {
        string fullSource = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        string target = Path.Combine(destination, Path.GetFileName(fullSource));
        if ((File.GetAttributes(fullSource) & FileAttributes.ReparsePoint) != 0) throw new Exception("不复制符号链接或目录联接：" + source);
        if (Directory.Exists(source)) {
            if ((Path.GetFullPath(destination) + Path.DirectorySeparatorChar).StartsWith(fullSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("不能把文件夹拖入自身。");
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("目标已存在：" + target);
            Directory.CreateDirectory(target);
            foreach (string child in Directory.GetFileSystemEntries(source)) CopyIn(child, target);
        } else File.Copy(source, target, false);
    }
    public static string SaveImage(Image image, string localRoot, string tempRoot) {
        string relative = "claude-inbox/shot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".png";
        string path = Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)); Directory.CreateDirectory(tempRoot);
        // Same-volume rename is atomic, including when projects are on D:.
        // Syncthing reserves and ignores its .syncthing.* temporary files.
        string temp = Path.Combine(Path.GetDirectoryName(path), ".syncthing." + Guid.NewGuid().ToString("N") + ".tmp");
        try { image.Save(temp, ImageFormat.Png); File.Move(temp, path); } finally { if (File.Exists(temp)) File.Delete(temp); }
        return relative;
    }
    async Task PasteScreenshot() {
        RequireProfile(); if (!ready || !profile.Enabled) throw new Exception("先启用同步，再粘贴截图。");
        if (!Clipboard.ContainsImage()) throw new Exception("剪贴板中没有图片，请先截图或复制图片。");
        string relative;
        using (var image = Clipboard.GetImage()) { if (image == null) throw new Exception("读取剪贴板图片失败。"); relative = SaveImage(image, profile.LocalRoot, Program.DataDir); }
        var item = new AttachmentItem { Relative = relative, FolderId = profile.FolderId, Device = profile.RemoteDevice, RemotePath = SyncEngine.RemotePath(profile, relative) };
        pending.Add(item); PersistQueue(); attachment.Text = "截图已保存，等待服务器接收：" + relative;
        await engine.Scan(profile, relative); RefreshFiles();
    }
    void PersistQueue() { AtomicWrite(queueFile, SyncEngine.Json.Serialize(pending)); }
    AttachmentItem LastReady() {
        var item = pending.LastOrDefault(x => x.FolderId == profile.FolderId && x.Device == profile.RemoteDevice);
        if (item == null || !item.Ready) throw new Exception("最新截图还没有确认到达服务器。请等待在线状态，程序会自动检查。"); return item;
    }
    async Task CopyAttachment() { Clipboard.SetText(LastReady().RemotePath); attachment.Text = "已复制远端路径，可粘贴给 Claude。"; await Task.FromResult(0); }
    async Task InsertAttachment() { terminal.Insert("请查看这张图片：\"" + LastReady().RemotePath.Replace("\"", "\\\"") + "\" "); await Task.FromResult(0); }
}

public class AttachmentItem { public string Relative; public string FolderId; public string Device; public string RemotePath; public bool Ready; }

sealed class SyncSetup : Form {
    public SyncProfile Result;
    readonly TextBox host = new TextBox(), user = new TextBox(), remote = new TextBox(), local = new TextBox(), peer = new TextBox();
    readonly NumericUpDown port = new NumericUpDown { Minimum = 1, Maximum = 65535 };
    public SyncSetup(SyncProfile p) {
        Text = "项目与服务器配置"; ClientSize = new Size(680, 430); StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 9); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        Add("服务器 IP / 主机名", host, 20); Add("SSH 用户名", user, 65); Add("SSH 端口", port, 110); Add("服务器项目目录", remote, 155); Add("本机项目目录", local, 200); Add("服务器设备 ID", peer, 245);
        host.Text = p.Host; user.Text = p.User; port.Value = p.SshPort; remote.Text = p.RemoteRoot; local.Text = p.LocalRoot; peer.Text = p.RemoteDevice;
        local.Width = 385; var browse = new Button { Text = "选择", Left = 575, Top = 198, Width = 70 }; browse.Click += delegate { using (var d = new FolderBrowserDialog()) { d.Description = "选择专用项目目录；其中的文件将与服务器双向同步。"; if (d.ShowDialog(this) == DialogResult.OK) local.Text = d.SelectedPath; } }; Controls.Add(browse);
        Controls.Add(new Label { Text = "首次设置可先留空设备 ID，导出并运行服务器脚本后再填。\n同步代码、文档和素材；依赖、Git 数据、凭据和密钥默认排除。\n此处不保存 SSH 密码，连接时由内置终端交互认证。", Left = 20, Top = 295, Width = 630, Height = 65 });
        var save = new Button { Text = "保存", Left = 545, Top = 380, Width = 100 }; save.Click += delegate {
            try { Result = new SyncProfile { Host = host.Text.Trim(), User = user.Text.Trim(), SshPort = (int)port.Value, RemoteRoot = remote.Text.Trim(), LocalRoot = local.Text.Trim(), RemoteDevice = peer.Text.Trim().ToUpperInvariant(), FolderId = p.FolderId, Enabled = p.Enabled }; SyncEngine.Validate(Result, false); DialogResult = DialogResult.OK; Close(); } catch (Exception e) { MessageBox.Show(this, e.Message); }
        }; Controls.Add(save); AcceptButton = save;
    }
    void Add(string title, Control input, int y) { Controls.Add(new Label { Text = title, Left = 20, Top = y + 5, Width = 155 }); input.SetBounds(180, y, 465, 28); Controls.Add(input); }
}
