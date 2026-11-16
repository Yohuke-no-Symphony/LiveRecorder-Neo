# 转码 Worker

独立转码节点，通过 TCP 接收任务，通过 HTTP/HTTPS 下载原片并上传 H.264/AAC HLS 成品。无需 MongoDB、入站端口或共享磁盘。

## 快速部署

1. 安装 FFmpeg、ffprobe 和所需 GPU 驱动，从 [Releases](https://github.com/Yohuke-no-Symphony/LiveRecorder-Neo/releases/latest) 下载对应平台的 `workers-<版本>-<平台>.zip`，解压后进入 `transcoding/` 目录。发布包自带 .NET 运行时，未提供的平台可[从源码发布](#从源码发布)。
2. 在主站「管理 → Worker → 转码」创建 Worker，保存节点 ID 与一次性 Secret；确认网站地址和 TCP 控制端口可达，默认控制端口为 `4790`。
3. 在解压目录运行：

   ```sh
   chmod +x ./ylr-tc-worker
   ./ylr-tc-worker register
   ./ylr-tc-worker run
   ```

   Windows 跳过 `chmod`，使用 `.\ylr-tc-worker.exe`。注册向导填写主站地址、控制地址、TLS 选项、ID 和 Secret，并配置临时目录及媒体工具；Ctrl+C 停止前台运行。

网站 HTTP/HTTPS 与 TCP 控制通道分别配置。主站 `TranscodingControl:UseTls` 与节点 `ControlUseTls` 必须一致；内网不使用证书时均设为 `false`。HTTP 反向代理不能直接转发 TCP 控制端口。

## 配置

注册后将连接信息和运行参数保存到可执行文件旁的 `settings.json`，也可指定固定路径：

```sh
./ylr-tc-worker register --config /absolute/path/settings.json
./ylr-tc-worker config --config /absolute/path/settings.json
./ylr-tc-worker run --config /absolute/path/settings.json
```

`config` 修改 FFmpeg、ffprobe、临时目录、并发、设备、缓存和传输限额。路径属于节点机器；运行账户须可读写设置文件与临时目录。临时空间应能容纳完整原片和转码输出。

主站后台与本机共用同一份运行参数，节点会同步并保存；运行中通过主站修改，本机用 `config` 修改前先停服务。发生版本冲突时，运行 `config` 读取主站最新配置并重新确认。

无人值守部署可复制 `settings.example.json`，填写 `Worker` 地址和凭据，以及 `Configuration.TempDirectory` 绝对路径。配置含 Secret，不要提交到仓库。旧版 Worker 的 `appsettings.json` 需改名为 `settings.json`。

启用控制 TLS 时，主站使用含私钥的 PFX 证书。系统可信证书无需指定指纹；私有证书可配置 `ControlCertificateSha256`、`ApiCertificateSha256`，分别固定控制与文件接口证书，仍校验有效期与主机名。

## 系统服务

将程序放在固定目录，在管理员终端执行；macOS/Linux 使用 `sudo`，Windows 使用管理员 PowerShell：

```sh
sudo ./ylr-tc-worker install --config /absolute/path/settings.json
sudo ./ylr-tc-worker enable
sudo ./ylr-tc-worker start
```

安装后默认停止且不开机启动，`start` 和 `enable` 分别控制运行与开机启动。

| 命令 | 用途 |
| --- | --- |
| `register` / `config` / `run` | 绑定主站、修改参数、前台运行 |
| `install` / `uninstall` | 安装或移除系统服务，卸载保留配置和缓存 |
| `start` / `stop` | 启停服务，停止时等待 FFmpeg 退出 |
| `enable` / `disable` | 开关开机启动 |
| `status` / `log` | 查看服务状态、跟随日志，Ctrl+C 退出日志 |
| `help` / `version` | 查看帮助与版本 |

Linux 使用 systemd，Windows 使用 Windows Service，服务名为 `yohuke-live-recorder-worker`；macOS 使用 LaunchDaemon，标签为 `com.yohuke.live-recorder.worker`。Unix 服务使用 root，Windows 使用 LocalSystem；工具路径建议填写绝对路径。

升级时先 `stop`，替换程序后 `start`；修改程序或配置位置后重新安装服务。主站节点卡片可查看任务日志，保留 7 天。命令行提示与日志使用英文。

## 编码与资源

| 系统 | 探测的硬件编码后端 |
| --- | --- |
| Windows | NVENC、Intel QSV、AMD AMF |
| Linux | NVENC、QSV、VAAPI、AMF |
| macOS | VideoToolbox |

FFmpeg 需包含对应编码器，驱动与设备权限需可用；Linux QSV/VAAPI 还需访问渲染设备节点。节点通过短样本编码后才接单。

默认总并发 2、GPU 并发 1、CPU 补位 1。CPU 补位需等待 GPU 超过 60 秒，且最近 30 秒平均 CPU 使用率低于 60%；这些阈值可在配置中调整，不是 CPU 使用率硬上限。

转码保留原片和画面比例，不提高源码率、帧率或分辨率；HDR 或无法确认源码率的输入会跳过。主站配置 HLS 档位、分片时长、关键帧间隔和单文件存储。

下载和上传支持断点续传，断网后自动重连。同一原片可复用输入缓存，空间不足时清理未使用的缓存；更换临时目录后，旧缓存需自行清理。

## 从源码发布

在仓库根目录执行，需要 .NET 10 SDK 和 PowerShell：

```powershell
pwsh ./release-worker.ps1 -Runtime win-x64 --version 0.2.0
pwsh ./release-worker.ps1 --all --version 0.2.0
```

默认 `linux-x64`，支持 `linux-arm64`、`win-x64`、`osx-x64`、`osx-arm64`；`--version` 可省略，省略时读取转码项目的版本，并用于包内所有 Worker。每个平台生成一个 `Artifacts/workers/<version>/workers-<version>-<runtime>.zip`，同时包含转码和切片程序。

发布出的散文件保留在 `Artifacts/workers/<version>/<runtime>/`，其下同样包含 `transcoding/` 和 `clip/`。ZIP 使用本次的干净发布内容生成，包内结构如下：

```text
workers-0.2.0-win-x64.zip
├── transcoding/
│   ├── ylr-tc-worker.exe
│   ├── settings.example.json
│   ├── README.md
│   └── LICENSE
└── clip/
    ├── ylr-clip-worker.exe
    ├── settings.example.json
    ├── README.md
    └── LICENSE
```
