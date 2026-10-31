using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

// PuTTY stays responsible for SSH, host-key verification and interactive login.
// No password is passed through process arguments, files or this application.
sealed class NativeTerminal : IDisposable {
    readonly Panel host;
    Process process;
    IntPtr child;
    string commandFile;
    string connection;
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr child);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    public NativeTerminal(Panel panel) { host = panel; host.Resize += delegate { Resize(); }; }
    public bool Running { get { return process != null && !process.HasExited; } }
    public bool Attached { get { return child != IntPtr.Zero && Running; } }
    public async Task Connect(SyncProfile p, bool startClaude = true) {
        string desired = p.User + "@" + p.Host + ":" + p.SshPort + "/" + startClaude;
        if (Running) { if (connection != desired) throw new Exception("右侧终端已有连接。请先点击断开终端，再切换账号或终端模式。"); Focus(); return; }
        string exe = Path.Combine(Program.BaseDir, "vendor", "putty", "putty.exe");
        if (!File.Exists(exe)) throw new Exception("缺少内置 SSH 终端，请获取完整发行包。");
        Directory.CreateDirectory(Program.DataDir);
        commandFile = Path.Combine(Program.DataDir, "terminal-" + Guid.NewGuid().ToString("N") + ".sh");
        string body = "export PATH=\"$HOME/.local/bin:$PATH\"; cd -- " + SyncEngine.ShellQuote(p.RemoteRoot) + " || exit; if ! command -v tmux >/dev/null; then echo '请先在服务器安装 tmux'; exec bash -l; fi; exec tmux new-session -A -s rpf 'claude'";
        if (!startClaude) body = "export PATH=\"$HOME/.local/bin:$PATH\"; exec bash -l";
        File.WriteAllText(commandFile, "exec bash -lc " + SyncEngine.ShellQuote(body) + "\n", new System.Text.UTF8Encoding(false));
        var start = new ProcessStartInfo(exe, "-ssh -t -P " + p.SshPort + " -l " + Program.Quote(p.User) + " -m " + Program.Quote(commandFile) + " " + Program.Quote(p.Host)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
        process = Process.Start(start);
        connection = desired;
        for (int i = 0; i < 200; i++) {
            if (host.IsDisposed) return;
            if (process.HasExited) throw new Exception("SSH 窗口已退出。请检查连接或认证信息。");
            process.Refresh(); child = process.MainWindowHandle;
            if (child != IntPtr.Zero) break;
            await Task.Delay(100);
        }
        if (child == IntPtr.Zero) throw new Exception("终端窗口尚未就绪；请检查是否有服务器指纹确认窗口。");
        int style = GetWindowLong(child, -16);
        SetWindowLong(child, -16, (style & ~unchecked((int)0x80C40000)) | 0x40000000);
        SetParent(child, host.Handle);
        int parentError = Marshal.GetLastWin32Error();
        if (GetParent(child) != host.Handle) throw new Exception("终端嵌入失败，Win32=" + parentError + "，窗口=" + process.MainWindowTitle);
        Resize(); Focus();
    }
    public void Resize() { if (child != IntPtr.Zero && !host.IsDisposed) MoveWindow(child, 0, 0, host.ClientSize.Width, host.ClientSize.Height, true); }
    public bool ParentMatches { get { return Attached && GetParent(child) == host.Handle; } }
    public void Focus() { if (Attached) SetFocus(child); }
    public void Insert(string text) {
        if (!Attached) throw new Exception("请先连接右侧终端。");
        if (text.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new Exception("插入内容不能包含换行。");
        Focus(); foreach (char c in text) SendMessage(child, 0x0102, (IntPtr)c, IntPtr.Zero);
    }
    public void Dispose() {
        // Terminate only the owned SSH client, avoiding an orphaned exit dialog.
        // tmux keeps the remote Claude process alive after SSH disconnects.
        if (Running) { try { process.Kill(); process.WaitForExit(2000); } catch { } }
        if (process != null) { process.Dispose(); process = null; }
        child = IntPtr.Zero; connection = null;
        if (commandFile != null) { try { File.Delete(commandFile); } catch { } }
    }
}
