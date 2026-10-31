# 远程项目工作台 v1.1.0

一个 Windows 窗口操作远程 Linux 项目：左侧浏览自动同步到本机的文件，右侧通过 SSH 使用 Claude Code，下方粘贴截图并插入远端图片路径。

## 日常使用

- 本机保存文件后，后台同步到服务器；Claude 生成的代码、文档和图片自动回到本机目录。
- 文件区支持目录导航、双击本地打开、拖入复制、拖出到其他应用；默认不覆盖同名文件。
- 点击“连接 Claude”，在同一窗口完成 SSH 登录并进入远端 tmux 会话。“SSH 命令行”提供普通远端 Shell。
- 截图后点击“粘贴截图”，或焦点在工作台控件时按 Ctrl+V。确认图片到达服务器后，点击“插入 Claude 输入框”，由你按回车发送。
- 关闭工作台会断开 SSH；tmux 中的远端 Claude 会话继续保留。同步引擎独立运行，关闭窗口后仍可同步。

## 首次设置

1. 完整解压发行 ZIP 到长期保留的位置，双击 `远程项目工作台.exe`。需要 Windows 10/11 x64 和 .NET Framework 4.8。
2. 点击“配置项目”，填写服务器、SSH 用户、端口、本机专用项目目录和对应远端目录。设备 ID 可先留空。例如本机 `D:\Projects\demo` 对应远端 `/home/你的账号/projects/demo`。不要同步整个磁盘或用户主目录。
3. 点击“① 导出服务器配置”。把生成的 `.sh` 上传到服务器，由 root 执行 `bash 生成的脚本.sh`。上传可使用“高级 SFTP”，执行可使用现有远程终端；这是首次设置。
4. 脚本使用现有普通账号运行独立同步服务，并输出服务器设备 ID。把 ID 填回“配置项目”。
5. 云安全组和启用的主机防火墙须允许 TCP 22020 入站。18385 管理端口仅供服务器本机访问，不要开放到公网。
6. 点击“② 启用同步”，核对两端目录。等状态显示服务器在线、本机和远端均无待同步项，再开始任务。
7. 点击“连接 Claude”。首次服务器指纹确认和密码输入由 PuTTY 处理，程序不保存 SSH 密码。服务器应已给该用户安装 Claude Code 并完成订阅账号登录。

首次设置后，日常无需同时打开 WinSCP 和 Xshell。“高级 SFTP”只是可选的远端文件浏览入口。

## 截图传递

图片保存为项目下的 `claude-inbox/shot-时间-随机ID.png`，由后台同步。

程序查询 Syncthing 文件可用性，确认指定服务器报告完整文件后，才允许复制路径或插入终端。离线时保留图片和待处理记录，恢复连接后继续确认。

插入的是“请查看这张图片：远端文件路径”，由 Claude Code 读取图片；不使用终端图像协议，不自动回车或发送消息。

PuTTY 拥有自己的键盘焦点，Ctrl+V 无法跨进程统一拦截。焦点在右侧终端时，请点击下方“粘贴截图”按钮。

## 同步规则

使用 Syncthing 2.1.7 的双向模式，文件监听加 60 秒兜底扫描。同步有延迟；本机和服务器须同时在线才能交换文件。

- 排除 `.git`、`node_modules`、`.venv`、`venv`、`__pycache__`、`.next`、`.cache`、`.env` / `.env.*`、`.ssh`、`.claude`、`.codex` 和常见密钥扩展名。
- `.stignore` 两端分别生成，并保留已有规则。默认规则先匹配；如有需要，应在两端调整。`.env.example` 也默认排除。
- 修改、重命名和删除都会同步。两边同时改同一文件时保留 `sync-conflict` 文件，不自动合并代码。
- 两端各保留 20 份收到的旧版本，仅保护接收其他设备修改时被替换或删除的文件。同步和版本保留不能代替备份，代码仍需 Git。
- Windows/Linux 的大小写、符号链接、权限和文件名规则不同。依赖分别安装，不同步运行中的数据库。
- 等同步完成后，再让 Agent 接手同一项目。并行编辑时，给 Agent 独立任务目录，通过 Diff / Git 合并。

## 终端与后台进程

Claude 模式进入项目目录，用 `tmux new-session -A -s rpf` 创建或恢复会话。当前每个账号使用一个 `rpf` 会话，只支持一个配置的项目。

服务器重启后，tmux 不会恢复此前进程。普通 SSH 命令行中的前台任务，也不保证断线后继续。切换终端模式或账号前，先点击“断开终端”。

- 配置：`%LOCALAPPDATA%\RemoteProjectFolder\sync-profile.json`，无 SSH 密码。
- 截图队列：同目录的 `pending-attachments.json`。
- 同步实例：同目录 `sync-engine`，含设备私钥、API 密钥和数据库，不应分享。
- 本机管理页：`http://127.0.0.1:18484`；本机同步监听仅回环 `22121`。
- 服务器实例：普通账号的 `~/.local/state/remote-project-sync`；服务名 `remote-project-sync-用户名.service`。
- 使用静态服务器地址和设备证书配对，关闭公网发现、局域网发现、中继和 NAT 穿透。
- 服务器服务开机自启。本机重启后需重新打开工作台，此版本不自动添加开机启动项。
- “同步高级管理”的 Actions 菜单可关闭本机同步引擎；关闭普通工作台窗口不会关闭引擎。

高级 SFTP 继承 v1.0 的独立 WinSCP 配置和密码机制，见 [原说明](docs/v1.0-README.md)。该入口不参与双向同步。

## 构建与检查

```powershell
./tools/fetch-dependencies.ps1
./build.ps1
```

固定依赖版本并核对 SHA-256，同时检查 PuTTY Authenticode 签名。构建使用 Windows 自带 .NET Framework C# 编译器。

```powershell
Start-Process './远程项目工作台.exe' -ArgumentList '--test-workbench','workbench-test.txt' -Wait
Start-Process './远程项目工作台.exe' -ArgumentList '--test-sync','sync-test.txt' -Wait
Start-Process './远程项目工作台.exe' -ArgumentList '--test-terminal','terminal-test.txt' -Wait
./tools/package.ps1
```

集成测试使用回环 TCP 22020、22021 和管理端口 28384、28385，不应与占用同端口的实例同时运行。测试完成后关闭测试节点。

GitHub Actions 可以编译、检查并生成 Windows 程序包。源码仓库不提交二进制或个人配置；发行包包含运行组件和许可。工作台本身未签名。

实际验证及限制见 [验证记录](验证记录.md)。

