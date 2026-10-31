# 第三方组件

## PuTTY 0.85

内置 SSH 终端，独立进程嵌入工作台，不修改二进制。

- 官网：https://www.chiark.greenend.org.uk/~sgtatham/putty/
- 固定版本：https://the.earth.li/~sgtatham/putty/0.85/
- MIT 许可见 `vendor/putty/LICENCE`。
- 下载脚本核对 SHA-256 和 Authenticode 签名。

## Syncthing 2.1.7

通过本机 REST API 提供双向同步，不修改二进制。

- 对应源码：https://github.com/syncthing/syncthing/tree/v2.1.7
- 发行：https://github.com/syncthing/syncthing/releases/tag/v2.1.7
- MPL 2.0 许可和贡献者见 `vendor/syncthing/LICENSE.txt`、`AUTHORS.txt`。对应源代码可通过上述版本源码地址获取。
- Windows 下载脚本和服务器脚本均核对固定版本 SHA-256。

## WinSCP 6.7

只用于可选的高级 SFTP 窗口，保留 v1.0 功能。

- 官网：https://winscp.net/
- 对应源码：https://github.com/winscp/winscp/tree/6.7
- GPL v3 或更新版本及依赖许可见 `vendor/winscp/license.txt`。
- 对应完整源码随发行包提供在 `third-party-source/WinSCP-6.7-Source.zip`；源码仓库通过依赖脚本下载。
- 原用户发行包含简体中文资源；重新下载便携版时，高级 SFTP 可能显示英文。工作台自身界面为中文。

启动器代码使用 MIT 许可，见 LICENSE.txt。组件各自的许可分别适用。
