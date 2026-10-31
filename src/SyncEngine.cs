using System;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;

public class SyncProfile {
    public string Host = "";
    public string User = "";
    public int SshPort = 22;
    public string RemoteRoot = "";
    public string LocalRoot = "";
    public string RemoteDevice = "";
    public string FolderId = "remote-project";
    public bool Enabled = false;
}

public sealed class SyncEngine {
    public const string IgnoreText = ".git\nnode_modules\n.venv\nvenv\n__pycache__\n.next\n.cache\n.env\n.env.*\n.ssh\n.claude\n.codex\n*.pem\n*.key\n*.p12\n*.pfx\n";
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16777216 };
    public readonly string Home;
    public readonly string BaseUrl;
    readonly string exe;
    readonly int listenPort;
    public string DeviceId { get; private set; }
    public string ApiKey { get; private set; }
    public SyncEngine(string exePath, string home, int guiPort, int syncPort) {
        exe = exePath; Home = home; listenPort = syncPort;
        BaseUrl = "http://127.0.0.1:" + guiPort;
    }
    public static Dictionary<string, object> Obj(object x) { return (Dictionary<string, object>)x; }
    public static object[] Arr(object x) { return (object[])x; }
    public static string Str(Dictionary<string, object> x, string k) { return x.ContainsKey(k) && x[k] != null ? Convert.ToString(x[k]) : ""; }
    public static void Validate(SyncProfile p, bool requirePeer) {
        if (String.IsNullOrWhiteSpace(p.Host) || p.Host.Any(c => Char.IsWhiteSpace(c) || "/\\\"@[]".Contains(c))) throw new Exception("请填写服务器 IP / 主机名。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(p.User, "^[a-z_][a-z0-9_-]*[$]?$")) throw new Exception("请填写有效的 Linux 用户名。");
        if (p.SshPort < 1 || p.SshPort > 65535) throw new Exception("SSH 端口范围为 1 到 65535。");
        if (!p.RemoteRoot.StartsWith("/") || p.RemoteRoot.Any(Char.IsControl) || p.RemoteRoot.Split('/').Contains("..")) throw new Exception("服务器项目目录必须是绝对路径，不能包含 ..。");
        if (p.RemoteRoot.TrimEnd('/') == "" || p.RemoteRoot.TrimEnd('/') == "/home" || p.RemoteRoot.TrimEnd('/') == "/home/" + p.User || p.RemoteRoot.TrimEnd('/') == "/root") throw new Exception("请选择专用项目子目录，不要同步系统目录或整个用户主目录。");
        if (!Path.IsPathRooted(p.LocalRoot) || p.LocalRoot.StartsWith(@"\\") || p.LocalRoot.Contains("\"")) throw new Exception("请选择本机磁盘上的专用项目目录。");
        string full = Path.GetFullPath(p.LocalRoot).TrimEnd(Path.DirectorySeparatorChar);
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar);
        if (full == Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar) || String.Equals(full, userHome, StringComparison.OrdinalIgnoreCase)) throw new Exception("不能同步整个磁盘或用户主目录。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(p.FolderId, "^[a-zA-Z0-9_-]{1,64}$")) throw new Exception("无效的同步目录 ID。");
        if ((requirePeer || p.RemoteDevice.Length > 0) && !System.Text.RegularExpressions.Regex.IsMatch(p.RemoteDevice, "^[A-Z2-7]{7}(-[A-Z2-7]{7}){7}$")) throw new Exception("请粘贴服务器返回的完整 Syncthing 设备 ID。");
    }
    ProcessStartInfo StartInfo(string args) {
        return new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) };
    }
    public async Task Start() {
        if (!File.Exists(exe)) throw new Exception("缺少同步引擎，请运行 tools/fetch-dependencies.ps1 或使用完整发行包。");
        Directory.CreateDirectory(Home);
        string config = Path.Combine(Home, "config.xml");
        if (!File.Exists(config)) {
            using (var p = Process.Start(StartInfo("generate --home=" + Program.Quote(Home)))) {
                await Task.Run(() => { if (!p.WaitForExit(30000)) { p.Kill(); throw new Exception("同步引擎初始化超时。"); } if (p.ExitCode != 0) throw new Exception("同步引擎初始化失败。"); });
            }
            var doc = new XmlDocument(); doc.Load(config);
            foreach (XmlNode folder in doc.SelectNodes("/configuration/folder")) folder.ParentNode.RemoveChild(folder);
            SetXml(doc, "/configuration/gui/address", new Uri(BaseUrl).Authority);
            SetXml(doc, "/configuration/options/listenAddress", "tcp://127.0.0.1:" + listenPort);
            foreach (string k in new[] { "globalAnnounceEnabled", "localAnnounceEnabled", "relaysEnabled", "natEnabled", "startBrowser" }) SetXml(doc, "/configuration/options/" + k, "false");
            SetXml(doc, "/configuration/options/urAccepted", "-1");
            SetXml(doc, "/configuration/options/autoUpgradeIntervalH", "0");
            foreach (XmlElement leaf in doc.SelectNodes("//*[not(*)]")) { if (String.IsNullOrWhiteSpace(leaf.InnerText)) { leaf.InnerText = ""; leaf.IsEmpty = true; } }
            using (var writer = XmlWriter.Create(config, new XmlWriterSettings { Indent = false, Encoding = new UTF8Encoding(false) })) doc.Save(writer);
        }
        var xml = new XmlDocument(); xml.Load(config);
        ApiKey = xml.SelectSingleNode("/configuration/gui/apikey").InnerText;
        try { await Call("GET", "/rest/system/status", null); }
        catch {
            using (var p = Process.Start(StartInfo("serve --home=" + Program.Quote(Home) + " --no-browser --no-console --no-upgrade --no-restart --log-file=" + Program.Quote(Path.Combine(Home, "syncthing.log")) + " --log-max-old-files=2 --log-max-size=1048576"))) { }
        }
        Exception last = null;
        for (int i = 0; i < 60; i++) {
            try { DeviceId = Str(Obj(await Call("GET", "/rest/system/status", null)), "myID"); return; }
            catch (Exception e) { last = e; }
            await Task.Delay(500);
        }
        throw new Exception("同步引擎未就绪。请检查本机端口和日志：" + Home, last);
    }
    static void SetXml(XmlDocument d, string path, string text) {
        XmlNode n = d.SelectSingleNode(path);
        if (n == null) { int i = path.LastIndexOf('/'); n = d.CreateElement(path.Substring(i + 1)); d.SelectSingleNode(path.Substring(0, i)).AppendChild(n); }
        n.InnerText = text;
    }
    public async Task<object> Call(string method, string path, object body) {
        if (!path.StartsWith("/rest/")) throw new ArgumentException("Invalid API path");
        var req = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
        req.ServicePoint.ConnectionLimit = 10;
        req.Proxy = null; req.Method = method; req.Headers["X-API-Key"] = ApiKey;
        req.Timeout = 5000; req.ReadWriteTimeout = 5000; req.AllowAutoRedirect = false;
        using (var deadline = new System.Threading.Timer(delegate { req.Abort(); }, null, 10000, System.Threading.Timeout.Infinite)) {
        if (body != null) {
            byte[] data = Encoding.UTF8.GetBytes(Json.Serialize(body)); req.ContentType = "application/json"; req.ContentLength = data.Length;
            using (var s = await req.GetRequestStreamAsync()) await s.WriteAsync(data, 0, data.Length);
        } else if (method == "POST" || method == "PUT" || method == "PATCH") req.ContentLength = 0;
        using (var response = await req.GetResponseAsync())
        using (var reader = new StreamReader(response.GetResponseStream())) {
            string text = await reader.ReadToEndAsync(); return String.IsNullOrWhiteSpace(text) ? new Dictionary<string, object>() : Json.DeserializeObject(text);
        }
        }
    }
    public async Task Configure(SyncProfile p) {
        Validate(p, true);
        Directory.CreateDirectory(p.LocalRoot);
        var existing = Arr(await Call("GET", "/rest/config/folders", null));
        foreach (var x in existing) {
            var f = Obj(x);
            if (Str(f, "id") == p.FolderId && !String.Equals(Path.GetFullPath(Str(f, "path")).TrimEnd('\\'), Path.GetFullPath(p.LocalRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new Exception("当前同步 ID 已绑定另一目录。请先在高级管理中暂停并移除旧绑定，再迁移目录。");
        }
        string ignores = Path.Combine(p.LocalRoot, ".stignore");
        // Preserve the user's ignore rules. Defaults precede old rules so secrets stay excluded.
        string prior = File.Exists(ignores) ? File.ReadAllText(ignores) : "";
        if (!prior.StartsWith(IgnoreText, StringComparison.Ordinal)) File.WriteAllText(ignores, IgnoreText + prior, new UTF8Encoding(false));
        var device = Obj(await Call("GET", "/rest/config/defaults/device", null));
        device["deviceID"] = p.RemoteDevice; device["name"] = "Linux server";
        device["addresses"] = new[] { "tcp://" + p.Host + ":22020" }; device["autoAcceptFolders"] = false;
        await Call("POST", "/rest/config/devices", device);
        var folder = Obj(await Call("GET", "/rest/config/defaults/folder", null));
        folder["id"] = p.FolderId; folder["label"] = "Remote project"; folder["path"] = Path.GetFullPath(p.LocalRoot);
        folder["type"] = "sendreceive"; folder["paused"] = false; folder["ignorePerms"] = true;
        folder["rescanIntervalS"] = 60; folder["fsWatcherEnabled"] = true; folder["fsWatcherDelayS"] = 2;
        folder["devices"] = new[] { new { deviceID = DeviceId, encryptionPassword = "", introducedBy = "" }, new { deviceID = p.RemoteDevice, encryptionPassword = "", introducedBy = "" } };
        folder["versioning"] = new { type = "simple", @params = new { keep = "20", cleanoutDays = "0" }, fsPath = "", fsType = "basic", cleanupIntervalS = 3600 };
        await Call("POST", "/rest/config/folders", folder);
    }
    public Task Pause(SyncProfile p, bool paused) {
        return Call("PATCH", "/rest/config/folders/" + Uri.EscapeDataString(p.FolderId), new { paused = paused });
    }
    public Task Scan(SyncProfile p, string sub) {
        return Call("POST", "/rest/db/scan?folder=" + Uri.EscapeDataString(p.FolderId) + "&sub=" + Uri.EscapeDataString(sub), null);
    }
    public async Task<bool> IsRemoteAvailable(SyncProfile p, string relative) {
        var file = Obj(await Call("GET", "/rest/db/file?folder=" + Uri.EscapeDataString(p.FolderId) + "&file=" + Uri.EscapeDataString(relative), null));
        if (!file.ContainsKey("availability") || file["availability"] == null) return false;
        foreach (var x in Arr(file["availability"])) {
            var a = Obj(x);
            if (Str(a, "id") == p.RemoteDevice && a.ContainsKey("fromTemporary") && !(bool)a["fromTemporary"]) return true;
        }
        return false;
    }
    public static string RemotePath(SyncProfile p, string relative) {
        if (relative.StartsWith("/") || relative.Split('/', '\\').Contains("..")) throw new Exception("无效的相对路径。");
        return p.RemoteRoot.TrimEnd('/') + "/" + relative.Replace('\\', '/');
    }
    public static string ShellQuote(string x) { return "'" + x.Replace("'", "'\"'\"'") + "'"; }
    public string ExportServerScript(SyncProfile p, string template) {
        Validate(p, false);
        return "#!/usr/bin/env bash\nexport RPF_USER=" + ShellQuote(p.User) + "\nexport RPF_ROOT=" + ShellQuote(p.RemoteRoot) + "\nexport RPF_DEVICE=" + ShellQuote(DeviceId) + "\nexport RPF_FOLDER=" + ShellQuote(p.FolderId) + "\n" + template.Replace("\r\n", "\n");
    }
}
