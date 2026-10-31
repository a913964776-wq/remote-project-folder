using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

static class TerminalTests {
    [STAThread] public static int Run(string[] args) {
        string report = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Program.BaseDir,"terminal-test.txt");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TcpClient client = null;
        var server = Task.Run(async () => { client = await listener.AcceptTcpClientAsync(); byte[] banner = Encoding.ASCII.GetBytes("SSH-2.0-RPF-embedding-test\r\n"); await client.GetStream().WriteAsync(banner,0,banner.Length); await Task.Delay(20000); });
        Application.EnableVisualStyles();
        var form = new Form { Text = "RPF terminal embedding test", Width = 900, Height = 600 };
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black }; form.Controls.Add(panel);
        var terminal = new NativeTerminal(panel); int code = 1;
        form.Shown += async delegate {
            try {
                await terminal.Connect(new SyncProfile { Host = "127.0.0.1", User = "test", SshPort = port, RemoteRoot = "/home/test/project" });
                if (!terminal.ParentMatches) throw new Exception("child parent mismatch");
                form.Width = 1100; form.Height = 700; terminal.Resize();
                File.WriteAllText(report,"PASS: owned PuTTY window embedded in panel; resize succeeded; no account authentication performed."); code = 0;
                await Task.Delay(3000);
            } catch (Exception e) { File.WriteAllText(report,"FAIL: " + e); }
            terminal.Dispose(); form.Close();
        };
        Application.Run(form); listener.Stop(); if (client != null) client.Close(); return code;
    }
}
