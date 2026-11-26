# 开发与部署

项目介绍和安装步骤见[仓库首页](https://github.com/Yohuke-no-Symphony/LiveRecorder-Neo#快速部署)。本页同时随服务端发布包提供。

## 源码开发

需要 .NET 10 SDK、Node.js 22.12+、pnpm、MongoDB 8.x，以及支持 H.264/AAC 的 FFmpeg 和 ffprobe。以下命令从仓库根目录执行。

```sh
git submodule update --init --recursive
dotnet run --project Sources/Yohuke.LiveRecorder.Server -- --urls http://127.0.0.1:4784 --config /绝对路径/settings.json
```

另开终端启动前端：

```sh
cd Sources/WebUI
pnpm install --frozen-lockfile
pnpm dev
```

访问 `http://127.0.0.1:5784`，首次使用按安装向导完成配置。后端地址默认是 `http://127.0.0.1:4784`；需要修改时，在 `Sources/WebUI/.env.development.local` 设置 `VITE_API_BASE_URL` 并重启前端。

前后端使用相同主机名与协议。更换前端端口时，同步修改服务端 `appsettings.Development.json` 中的 `WebUI:DevelopmentOrigins`。开发环境的 OAuth 回调地址为 `http://127.0.0.1:5784/oauth/callback/提供方标识`。

## 发布

从仓库根目录执行：

```powershell
pwsh ./release.ps1 --version 1.2.3
pwsh ./release.ps1 -Runtime win-x64 --version 1.2.3
pwsh ./release.ps1 --all --version 1.2.3
```

默认目标为 `linux-x64`，还支持 `linux-arm64`、`win-x64`、`osx-x64`、`osx-arm64`。`--all` 发布全部平台，前端只构建一次；`--version` 可省略，指定时不修改项目文件。Bash 可使用 `./scripts/publish.sh linux-x64`。

产物目录为 `Artifacts/server/<version>/<runtime>/`，压缩包为 `Artifacts/server/<version>/server-<version>-<runtime>.zip`，解压后直接得到运行文件。程序自带 .NET 运行时；保留同目录的 `webui/` 和 `appsettings*.json`，FFmpeg 与 ffprobe 另行安装。

独立 Worker 使用 `pwsh ./release-worker.ps1 -Runtime win-x64 --version 1.2.3` 发布，支持同样的平台和 `--all` 参数。每个平台生成一个 `Artifacts/workers/<version>/workers-<version>-<runtime>.zip`，包内 `transcoding/`、`clip/` 和 `recording/` 分别提供转码、切片和录像程序及其部署说明。

## 部署与配置

在发布目录运行，确保运行账户可以写入设置文件、数据目录和录像目录：

```sh
./Yohuke.LiveRecorder.Server --urls http://127.0.0.1:4784 --config /var/lib/live-recorder/settings.json
```

Windows 使用 `Yohuke.LiveRecorder.Server.exe`。首次启动会在终端输出安装令牌；安装向导配置管理员、MongoDB、存储目录及媒体工具。全新安装使用空数据库，恢复实例使用原数据库与已有管理员账号。

### 设置与数据

`--config` 指定设置文件，省略时使用可执行文件旁的 `settings.json`。数据与录像目录可放在不同磁盘；相对路径以设置文件所在目录为基准。

| 位置 | 内容 |
| --- | --- |
| `settings.json` | 存储路径和后台设置 |
| 数据目录 | `bootstrap.json`、安装令牌、`keys/`、`cache/`、`ai/snapshots/` |
| 录像目录 | 按主播和场次保存的录像、弹幕、分析、切片，以及 `.derived/`、`.work/`、`.trash/` |
| MongoDB | 网站账号、B 站账号、直播间、录像索引和任务 |

默认数据目录为设置文件旁的 `Data/`，录像目录为 `Data/records/`。SMTP、OAuth、AI 接入、注册权限、带宽和保留期限均在后台设置。OAuth 回调为 `https://你的域名/oauth/callback/提供方标识`；邮件找回密码还需配置站点公开地址和 SMTP。

任务日志保留期限位于「设置 → 媒体与存储 → 保留时间」，对应 `settings.json` 的 `taskLogRetentionDays`，默认 `7`，允许 `0` 至 `3650`，`0` 表示长期保留。通过后台保存后无需重启，按日志原接收时间或历史录像结束时间应用于尚存日志；任务记录不随日志过期删除，已清理的日志无法恢复。

### 反向代理与系统服务

对外提供 HTTPS，转发原始 `Host`、`X-Forwarded-For` 和 `X-Forwarded-Proto`，并支持 `/hubs/status` 的 WebSocket 连接。在服务端 `appsettings.json` 的 `KnownProxies` 数组中填写实际连接主站的代理 IP。

媒体路径 `/api/playback/`、`/api/clips/`、`/api/jobs/`、`/api/sessions/` 应关闭代理缓存和响应缓冲。Nginx 对应配置为 `proxy_cache off;` 与 `proxy_buffering off;`，保留鉴权 Cookie。

Linux systemd 示例：[live-recorder.service](https://github.com/Yohuke-no-Symphony/LiveRecorder-Neo/blob/main/Sources/deploy/live-recorder.service)。使用前创建运行账户，按实际部署位置修改工作目录、程序和设置路径。

### 备份、升级与迁移

备份 `settings.json`、数据目录、录像目录和 MongoDB。数据目录中的 `keys/` 用于解密凭据，必须一并保留；Windows 还需保留原运行账户的 DPAPI 密钥环境。

升级前停止服务，用新版本替换程序文件，保留原设置与数据。迁移或修改存储路径时，先停止录制与服务，复制文件、修改 `settings.json`，再启动；程序不会自动移动录像。设置文件丢失时在安装页选择「恢复实例」，连接原数据库并使用原管理员登录。

主站在启动或恢复实例时检查 MongoDB 中的数据版本，业务入口在所需迁移通过核验后开放。迁移失败时查看启动日志，处理原因后重启，主站从最近提交的断点继续。若需要回退，停服后将旧程序和升级前同一批次的设置、数据目录、录像目录及 MongoDB 一起恢复。

旧版 `--DataDirectory` 和 `LIVE_RECORDER_DATA_DIR` 已停用，改用 `--config`。旧数据目录中的 `bootstrap.json`、安装令牌和 `keys/` 原样保留，旧 `Data/settings.json` 或数据库全局设置可在恢复时导入。

## 分布式转码

转码需要独立 Worker。主站通过 TCP 下发任务，Worker 通过 HTTP/HTTPS 下载原片、上传 HLS 成品，无需共享磁盘或连接 MongoDB。

1. 在「设置 → 转码」配置清晰度和默认档位，在「管理 → Worker → 转码」创建节点，保存节点 ID 与一次性 Secret。
2. 主站 `appsettings.json` 的 `TranscodingControl` 配置控制监听，默认启用、端口 `4790`、不启用 TLS。Worker 的控制地址必须可以 TCP 直连，修改监听设置后重启主站。
3. 在节点上运行 `ylr-tc-worker register`，填写网站地址、控制地址和凭据，再运行 `ylr-tc-worker run`。安装系统服务及 GPU 要求见 [Worker 部署说明](https://github.com/Yohuke-no-Symphony/LiveRecorder-Neo/blob/main/Sources/Worker/Yohuke.LiveRecorder.Transcoding.Worker/README.md)。

控制通道启用 TLS 时，主站配置包含私钥的 PFX 证书，Worker 的 `ControlUseTls` 与主站 `UseTls` 保持一致；PFX 密码可通过 `TranscodingControl__CertificatePassword` 设置。控制端口与网站 HTTPS 端口分别配置。

新录播结束后自动排队，历史录播需手动发起。原片保留，转码未完成或失败时回退原画；修改档位不会自动重转历史录像。成品保存在录像目录的 `.derived/`。

## 录制场次

录制场次在下播后先等待同场重连；期限内恢复或切换采集节点继续同一场。持续离线达到 `SessionGraceMinutes`，或持续无有效音视频达到 `StreamDisconnectTimeoutMinutes`，任一到期才结束。停录和节点交接只停止采集，重启不重置已经累计的等待时间。逻辑结束与归档补传完成分别判定。

## 测试

从仓库根目录按改动选择测试项目：

```sh
dotnet test Sources/Tests/Server/Yohuke.LiveRecorder.Tests.csproj
dotnet test Sources/Tests/Media/Yohuke.LiveRecorder.Media.Tests.csproj
dotnet test Sources/Tests/AI/Yohuke.LiveRecorder.AI.Tests.csproj
dotnet test Sources/Tests/TranscodingWorker/Yohuke.LiveRecorder.Transcoding.Worker.Tests.csproj
```

`Identity`、`Http`、`Grouping` 和 `Recording` 集成测试需要独立 MongoDB 实例，通过 `LIVE_RECORDER_TEST_MONGO` 指定连接；每次使用随机数据库名。例如：

```sh
LIVE_RECORDER_TEST_MONGO=mongodb://127.0.0.1:27017 dotnet test Sources/Tests/Http/Yohuke.LiveRecorder.Http.Tests.csproj
```

运行普通录制测试时，使用 `--filter 'FullyQualifiedName!~RecordingPressureTests'` 排除压力测试。需要压力验收时，设置 `LIVE_RECORDER_STRESS=1`，筛选 `FullyQualifiedName~RecordingPressureTests`。

前端测试与构建：

```sh
cd Sources/WebUI
pnpm test
pnpm build
```
