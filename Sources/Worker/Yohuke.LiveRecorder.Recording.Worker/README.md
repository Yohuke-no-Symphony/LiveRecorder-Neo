# 录像 Worker

录像节点负责采集视频和弹幕事件，将已归档内容上传主站。节点使用持久录制缓存保存尚未确认上传的数据，主站连接中断时继续采集已有房间，连接恢复后补传。

## 快速部署

1. 从 [Releases](https://github.com/Yohuke-no-Symphony/LiveRecorder-Neo/releases/latest) 下载对应平台的 `workers-<版本>-<平台>.zip`，解压后进入 `recording/` 目录。发布包自带 .NET 运行时。
2. 在主站「管理 → Worker → 录像」创建节点，保存节点 ID 与一次性访问密钥。
3. 运行注册向导，填写主站地址、控制地址、凭据和录制缓存目录，再启动节点。

   ```sh
   chmod +x ./ylr-rec-worker
   ./ylr-rec-worker register
   ./ylr-rec-worker run
   ```

   Windows 使用 `.\ylr-rec-worker.exe`，参数相同。Ctrl+C 停止前台运行。

节点通过 TCP 控制通道接收房间任务，默认控制端口为 `4790`。视频、索引和弹幕通过 HTTP/HTTPS 上传。主站 `TranscodingControl:UseTls` 与节点 `ControlUseTls` 需要一致，HTTP 反向代理需另外配置 TCP 转发才能承载控制通道。

## 录制缓存与补传

节点在本机保存视频、归档索引、弹幕及待上传状态，收到主站确认后清理已完成上传的内容。配置文件和录制缓存需要位于运行账户可持久读写的目录。

连接中断期间，已分配房间继续采集并保存到本机。恢复连接后，节点从主站已确认的偏移补传，并恢复采集状态的同步。主站播放和弹幕回放使用已上传的部分。

录制缓存达到空间上限或磁盘剩余空间低于最低要求时，节点停止采集并保留待确认内容，上传恢复后继续排空缓存。断连期间，已有房间的录制归属持续保留，主站等待原节点恢复并补传。

后台停用录像节点会停止当前采集，并允许其继续排空补传。已有录制归属释放后可以删除节点身份。断连中的节点会在恢复控制连接后接收停用状态。

## 配置

注册后的配置保存在程序旁的 `settings.json`，也可复制 `settings.example.json` 填写连接信息及录制缓存目录。

```sh
./ylr-rec-worker register --config /absolute/path/settings.json
./ylr-rec-worker config --config /absolute/path/settings.json
./ylr-rec-worker run --config /absolute/path/settings.json
```

| 配置字段 | 用途 |
| --- | --- |
| `Configuration.TempDirectory` | 持久录制缓存目录 |
| `Configuration.MaxConcurrentEncodes` | 同时采集的房间上限 |
| `Configuration.MaxCacheBytes` | 录制缓存空间上限 |
| `Configuration.MinimumFreeBytes` | 磁盘最低空闲空间 |
| `Configuration.UploadBytesPerSecond` | 上传带宽上限，`0` 表示不限速 |

主站节点卡片展示采集房间数、待上传字节、缓存占用和可用空间。运行参数可以在主站修改，节点同步配置版本。本机使用 `config` 前先停止服务，发生版本冲突时重新读取主站配置后保存。

启用控制 TLS 时，主站使用包含私钥的 PFX 证书。私有证书可以分别配置 `ControlCertificateSha256` 与 `ApiCertificateSha256`。配置文件包含节点访问密钥。

## 系统服务

将程序与配置放在固定目录，在管理员终端执行以下命令。macOS 和 Linux 使用 `sudo`，Windows 使用管理员 PowerShell。

```sh
sudo ./ylr-rec-worker install --config /absolute/path/settings.json
sudo ./ylr-rec-worker enable
sudo ./ylr-rec-worker start
```

安装后默认停止且不开机启动，通过 `start` 和 `enable` 分别控制。

| 命令 | 用途 |
| --- | --- |
| `register` / `config` / `run` | 绑定主站、修改参数、前台运行 |
| `install` / `uninstall` | 安装或移除系统服务 |
| `start` / `stop` | 启停采集与上传进程 |
| `enable` / `disable` | 开关开机启动 |
| `status` / `log` | 查看状态、跟随日志 |
| `help` / `version` | 查看帮助与版本 |

Linux 使用 systemd，Windows 使用 Windows Service，服务名为 `yohuke-live-recorder-recording-worker`。macOS 使用 LaunchDaemon，标签为 `com.yohuke.live-recorder.recording-worker`，服务文件和日志位于 `/Library/Application Support/Yohuke.LiveRecorder/RecordingWorkerService`。

Unix 服务使用 root，Windows 使用 LocalSystem，配置和缓存路径建议填写绝对路径。升级时先停止服务，替换程序后重新启动，保留配置和录制缓存即可继续补传。

## 从源码发布

在仓库根目录执行，需要 .NET 10 SDK 和 PowerShell。

```powershell
pwsh ./release-worker.ps1 -Runtime win-x64 --version 0.2.0
pwsh ./release-worker.ps1 --all --version 0.2.0
```

默认发布 `linux-x64`，还支持 `linux-arm64`、`win-x64`、`osx-x64` 和 `osx-arm64`。每个平台生成一个 `Artifacts/workers/<version>/workers-<version>-<runtime>.zip`，包内包含 `transcoding/`、`clip/` 和 `recording/`。录像程序、说明、配置示例和许可证位于 `recording/`，散文件保留在 `Artifacts/workers/<version>/<runtime>/recording/`。
