# 切片 Worker

独立切片节点，执行网站现有的精确和快速切片任务。主站通过 TCP 分发任务，节点按关键帧索引仅下载所需字节范围，生成 MP4 后回传主站验证和发布。每个任务的下载片段、重建输入和输出均在任务结束后清理。

## 快速部署

1. 安装 FFmpeg 和 ffprobe，从 [Releases](https://github.com/Yohuke-no-Symphony/LiveRecorder-Neo/releases/latest) 下载对应平台的 `workers-<版本>-<平台>.zip`，解压后进入 `clip/` 目录。发布包自带 .NET 运行时。
2. 在主站「管理 → Worker → 切片」创建 Worker，保存节点 ID 与一次性访问密钥。确认网站地址和 TCP 控制端口可达，默认控制端口为 `4790`。
3. 在解压目录运行以下命令。

   ```sh
   chmod +x ./ylr-clip-worker
   ./ylr-clip-worker register
   ./ylr-clip-worker run
   ```

   Windows 使用 `.\ylr-clip-worker.exe`，参数相同。注册向导设置主站地址、控制地址、TLS、节点凭据、媒体工具和临时目录。Ctrl+C 停止前台运行。

切片和转码节点使用同一个主站控制端口，各自创建 Worker 身份。网站 HTTP/HTTPS 与 TCP 控制通道分别配置，主站 `TranscodingControl:UseTls` 与节点 `ControlUseTls` 需要一致。HTTP 反向代理需要另外配置 TCP 转发才能承载控制通道。

## 配置

注册后的配置保存在程序旁的 `settings.json`。也可以复制 `settings.example.json`，填写 `Worker` 地址、凭据和 `Configuration.TempDirectory` 的绝对路径。

```sh
./ylr-clip-worker register --config /absolute/path/settings.json
./ylr-clip-worker config --config /absolute/path/settings.json
./ylr-clip-worker run --config /absolute/path/settings.json
```

`config` 可修改媒体工具、临时目录、切片任务并发、精确切片 CPU 编码线程数、临时空间上限、最低空闲空间及上传下载限速。空间预算按任务所需范围和输出计算。下载和上传限速为 `0` 时不限速。

`Configuration.MaxConcurrentEncodes` 设置同时运行的切片任务数，范围为 1–64。`Configuration.ClipEncodingThreads` 设置每个精确切片任务的 CPU 编码线程数，默认 2，范围为 1–128。运行多个精确切片任务时，各任务分别使用设置的线程数。

运行中可通过主站修改配置，节点同步并保存配置版本。本机执行 `config` 前先停止服务。发生版本冲突时重新执行 `config`，读取最新配置后保存。

启用控制 TLS 时，主站使用包含私钥的 PFX 证书。系统可信证书可直接使用，私有证书可分别设置 `ControlCertificateSha256` 和 `ApiCertificateSha256`。配置文件包含访问密钥，运行账户需要可读写该文件和临时目录。

## 切片与清理

精确模式保留请求时间范围和断流空档，快速模式按原有规则外扩到关键帧边界。录制中的素材仅使用创建任务时已归档的范围。

主站优先向可用切片节点派发带有效索引的任务。没有可用节点或源文件缺少有效索引时，继续由主站本机处理。

节点仅请求计划内的有限字节范围，并校验源版本及范围响应。上传可恢复到主站已确认的偏移，主站确认成品发布后完成任务。成功、失败、取消和租约失效都会释放任务临时文件，程序启动时回收异常退出留下且未被运行任务占用的目录。

## 系统服务

将程序与配置放在固定目录，在管理员终端运行以下命令。macOS 和 Linux 使用 `sudo`，Windows 使用管理员 PowerShell。

```sh
sudo ./ylr-clip-worker install --config /absolute/path/settings.json
sudo ./ylr-clip-worker enable
sudo ./ylr-clip-worker start
```

安装后默认停止且不开机启动，分别通过 `start` 和 `enable` 控制。

| 命令 | 用途 |
| --- | --- |
| `register` / `config` / `run` | 绑定主站、修改参数、前台运行 |
| `install` / `uninstall` | 安装或移除系统服务 |
| `start` / `stop` | 启停服务，停止时等待媒体进程退出 |
| `enable` / `disable` | 开关开机启动 |
| `status` / `log` | 查看状态、跟随日志 |
| `help` / `version` | 查看帮助与版本 |

Linux 使用 systemd，Windows 使用 Windows Service，服务名为 `yohuke-live-recorder-clipping-worker`。macOS 使用 LaunchDaemon，标签为 `com.yohuke.live-recorder.clipping-worker`，服务定义和日志位于 `/Library/Application Support/Yohuke.LiveRecorder/ClippingWorkerService`。两类 Worker 可以分别安装和启停。

Unix 服务使用 root，Windows 使用 LocalSystem，媒体工具和配置路径建议填写绝对路径。升级时先停止服务，替换程序后重新启动。主站节点卡片提供任务日志，命令行提示与日志使用英文。

## 从源码发布

在仓库根目录执行，需要 .NET 10 SDK 和 PowerShell。

```powershell
pwsh ./release-worker.ps1 -Runtime win-x64 --version 0.2.0
pwsh ./release-worker.ps1 --all --version 0.2.0
```

`release-clipping-worker.ps1` 保留为统一脚本的转调入口，也会打包所有 Worker。

默认发布 `linux-x64`，支持 `linux-arm64`、`win-x64`、`osx-x64` 和 `osx-arm64`。版本号可省略，省略时读取转码项目的版本，并用于包内所有 Worker。每个平台生成一个 `Artifacts/workers/<version>/workers-<version>-<runtime>.zip`，包内顶层目录为 `transcoding/`、`clip/` 和 `recording/`；切片程序及其说明、配置示例和许可证位于 `clip/`。

发布出的散文件保留在 `Artifacts/workers/<version>/<runtime>/`，其下同样包含 `transcoding/`、`clip/` 和 `recording/`。ZIP 使用本次的干净发布内容生成。
