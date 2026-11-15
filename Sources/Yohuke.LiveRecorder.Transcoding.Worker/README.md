# 转码 Worker

独立的 .NET 10 命令行节点，通过可选 TLS 的 TCP 控制通道接收主站任务，通过 HTTP 或 HTTPS 下载原片并回传 HLS 成品。Worker 不需要 MongoDB，不提供入站端口，也不共享主站磁盘。

## 启动与绑定

在主站后台创建 Worker，取得 Worker ID 和一次性 Secret，然后运行注册向导。

```sh
./ylr-worker register
```

Windows 使用 `ylr-worker.exe`。`register` 在终端询问主站 HTTP 或 HTTPS 地址，TCP 控制主机默认使用本次 URL 的主机名或 IP，并可手动填写独立地址。控制端口单独配置，默认 4790。向导会询问是否使用控制 TLS，HTTP 网站地址默认选择关闭，HTTPS 默认选择开启，随后填写 Worker ID 与 Secret，Secret 隐藏输入。连接信息与全部运行参数统一保存在可执行文件旁的 `settings.json`。首次连接后，若主站已配置运行参数则同步到该文件，本机与主站均未配置时继续进入运行参数向导。完成注册后退出。

命令帮助、交互提示、运行日志和错误输出均使用英文。无参数时显示帮助。

```sh
# Bind this worker to a server and synchronize its settings
./ylr-worker register

# Edit local runtime settings, including temporary storage and tool paths
./ylr-worker config

# Run in the foreground, use Ctrl+C to stop
./ylr-worker run

# Show command help and the embedded build version
./ylr-worker help
./ylr-worker help config
./ylr-worker version

# Select a settings file, before or after the command
./ylr-worker --config /path/to/settings.json run
```

`register` 先保存连接信息，再连接主站验证绑定并同步配置，主站暂时不可用时已经保存的绑定信息仍然保留。密钥轮换后停止 Worker，运行 `register` 输入新 Secret，再重新启动，同一绑定留空可保留已有密钥。

`config` 编辑 FFmpeg、ffprobe、临时目录、编码并发、设备和传输限额。已绑定时读取主站最新配置作为向导基础，保存到同一份 `settings.json` 并同步主站，执行时需要主站在线。未绑定时先保存本机运行参数，随后由 `register` 提交。一个命令只执行一个动作。

## 系统服务

`install` 自动识别操作系统，macOS 注册系统级 LaunchDaemon，Linux 注册 systemd 系统服务，Windows 注册 Windows Service。安装后处于停止、手动启动状态，`start` 启动服务，`enable` 设置开机启动。

| 命令 | 行为 |
| --- | --- |
| `install` | 记录当前可执行文件与绝对设置路径，安装系统服务 |
| `uninstall` | 停止服务并移除服务注册，保留设置和转码缓存 |
| `start` | 启动已安装服务 |
| `stop` | 停止服务并等待转码任务与 FFmpeg 进程退出 |
| `status` | 查看安装状态、运行状态、开机启动方式、进程 ID 和日志来源 |
| `log` | 显示最近 100 条服务日志并持续跟随新输出，Ctrl+C 退出 |
| `enable` | 开启下次开机自动启动 |
| `disable` | 关闭下次开机自动启动 |

`enable` 和 `disable` 修改开机启动策略，即时运行状态通过 `start` 和 `stop` 管理。服务控制命令可在 Worker 占用设置文件期间执行。`install` 的 `--config` 路径会写入服务定义，后续 `start` 使用该注册路径。

macOS 与 Linux 在固定部署目录使用管理员终端执行，下面的绝对设置路径需替换为实际位置。

```sh
sudo ./ylr-worker register --config /absolute/path/settings.json
sudo ./ylr-worker install --config /absolute/path/settings.json
sudo ./ylr-worker enable
sudo ./ylr-worker start

sudo ./ylr-worker status
sudo ./ylr-worker log

sudo ./ylr-worker stop
sudo ./ylr-worker config --config /absolute/path/settings.json
sudo ./ylr-worker start

sudo ./ylr-worker disable
sudo ./ylr-worker uninstall
```

Windows 在管理员 PowerShell 中执行对应的 `.\ylr-worker.exe` 命令。

Unix 系统服务使用 root，Windows 使用 LocalSystem。运行账户需要能够执行 Worker、FFmpeg 和 ffprobe，并读写设置文件所在目录与临时目录。运行中的设置同步会原子替换文件，后续 `register` 和 `config` 使用相同管理账户执行。工具安装在自定义目录时，通过 `config` 保存其绝对路径。

系统服务名为 `yohuke-live-recorder-worker`，macOS 的 launchd 标签为 `com.yohuke.live-recorder.worker`。Linux 日志可使用 `journalctl -u yohuke-live-recorder-worker` 查看，Windows 使用事件查看器中 Application 日志的同名来源。升级可执行文件时先 `stop`，替换后再 `start`，修改部署位置或设置路径时重新执行 `uninstall` 和 `install`。

`status` 查询已安装的系统服务，服务运行时退出码为 0，其余情况为 1。`log` 在 macOS 分别读取 `worker.log` 和 `worker-error.log` 的最近 100 行，在 Linux 跟随对应 unit 的 journal，在 Windows 读取 Application 日志中的 Worker 来源。日志查看持续运行，Ctrl+C 结束查看。查询使用系统中的服务注册信息和日志读取权限，macOS 已安装的系统服务通过管理员终端查询。

macOS 的服务定义、日志和 bundle 提取目录统一位于 `/Library/Application Support/Yohuke.LiveRecorder/WorkerService/`，开机启动定义位于 `/Library/LaunchDaemons/com.yohuke.live-recorder.worker.plist`。`worker.log` 与 `worker-error.log` 保存运行输出。Linux 的服务定义位于 `/etc/systemd/system/yohuke-live-recorder-worker.service`，bundle 提取目录为 `/var/cache/yohuke-live-recorder-worker/bundles`。

## 唯一设置文件

发布包提供 `settings.example.json`，包含全部连接与运行参数。交互注册时无需复制模板。无人值守部署可将模板复制为 `settings.json`，填写 `Worker` 内的地址和凭据，再运行 `run`。若在主站后台预先配置了运行参数，`Configuration` 可保留默认值，首次连接时自动同步。若在本地配置运行参数，必须填写本机绝对路径的 `TempDirectory`。

`Worker` 保存地址、`ControlUseTls`、Worker ID、Secret 与 TLS 证书设置。`Configuration` 保存 FFmpeg、ffprobe、临时目录、编码并发、GPU 偏好、CPU 补位策略、缓存与传输限额。程序会自动维护 `ConfigurationRevision`、`ConfigurationHash` 和 `BindingHash`，用于识别本地修改和主站配置版本，请保留这些同步字段。

主站后台下发的运行参数会写回同一份文件。停止 Worker 后可以直接修改 `Configuration`，下次运行会按已同步的版本提交。若本地与主站都修改了配置，将保留本地文件并报告版本冲突，运行 `config` 可以基于主站最新配置重新确认。保存采用同目录原子替换，同一文件只能由一个 Worker 进程占用，运行期间通过主站后台修改配置。

旧 Worker 的 `appsettings.json` 不再读取或打包。升级时可将旧文件改名为 `settings.json`，原有 `Worker` 连接信息会保留，首次连接会补齐主站运行参数。发布脚本只提供示例，不覆盖实际的 `settings.json`。Linux 和 macOS 上程序保存的设置文件权限为仅当前用户可读写；Windows 继承所选目录的权限，请将文件放在运行账户的私有目录中。配置文件含绑定密钥，不应提交到代码仓库。

`ApiBaseUrl` 支持 HTTP 和 HTTPS。主站 `appsettings.json` 的 `TranscodingControl` 节配置控制监听。内网免证书连接将主站的 `UseTls` 与 Worker 的 `ControlUseTls` 都设为 `false`，`CertificatePath` 可留空。默认控制端口为 4790，网站端口与 TCP 控制端口分别配置。修改主站监听配置后重启 Server。

启用控制 TLS 时，两端开关均设为 `true`，主站填写实际 PFX 证书路径及可选密码。系统可信证书无需填写指纹。私有证书可在绑定向导中填写其 DER 内容的 SHA-256 十六进制指纹，同时校验证书有效期与主机名。`TlsServerName` 用于 TCP 连接的证书主机名，HTTPS 按 URL 的主机名验证。旧设置省略新增开关时按 TLS 模式读取。

`ControlCertificateSha256` 与 `ApiCertificateSha256` 可分别固定两个端点的证书；未设置时回退到共同的 `ServerCertificateSha256`。例如 HTTPS 反代使用系统可信证书而控制端使用私有证书，只需设置控制端指纹。修改可选 TLS 参数时，留空保留现有值，输入 `-` 清除。

`run` 使用已保存的设置。缺少凭据或运行参数时输出配置指引并退出。启动阶段及运行中的网络中断均会自动重连，等待时间逐步增加到最多 30 秒。Ctrl+C 或 SIGTERM 会取消任务并等待当前 FFmpeg 进程树退出。

## 编码和资源

主站和 Worker 保留原片。转码提供额外的 H.264 8-bit/AAC HLS 档位：6 秒 fMP4 分片，2 秒关键帧间隔；保持横竖屏比例，目标高度按显示短边计算，尺寸取偶数。档位高于原片分辨率、HDR 输入或无法确认源码率时跳过；输出码率与帧率不超过源上限。原画仍走现有播放链路。

Windows 探测 NVENC、Intel QSV、AMD AMF；Linux 探测 NVENC、QSV、VAAPI 及 AMF；macOS 探测 VideoToolbox。所有候选先执行短样本编码，只有成功的后端可接单。FFmpeg 构建必须含对应编码器，并安装兼容驱动。Linux QSV/VAAPI 需要渲染设备节点访问权限。不同 GPU/驱动具有不同能力，发布成功不代表当前机器具有相应硬件。软件解码与缩放可以配合 GPU 编码。

默认总编码并发为 2，GPU 为 1，CPU 补位为 1。CPU 仅在输入下载、校验和探测完成后等待 GPU 超过 60 秒，且最近完整 30 秒 CPU 平均使用率低于 60% 时启动。采样缺失、失效或无法可靠计量受限 CPU 资源域时不启用补位。编码线程预算为有效逻辑核数的一半向下取整；不足 2 个有效核时不启用 CPU 补位。线程预算和启动阈值均不等于 CPU 使用率的硬上限。

GPU 恢复后正在运行的 CPU 任务继续完成，新任务优先 GPU。GPU 编码失败后暂时停用该设备；缺少可用编码器时每隔至少 60 秒重新检测，检测也占用同一执行槽，并等待 GPU 空闲。总编码并发包括 GPU 和 CPU；设为 1 时两者不会并行。降低并发只影响新任务，运行中的任务使用已确定的执行配置。工具与设备配置修改会等待 GPU 空闲，再重新检测能力。

输入下载支持 HTTP Range 续传，成品按最多 8 MiB 的数据块上传并按主站已确认偏移恢复。临时目录下仅维护 `live-recorder-worker/<WorkerId>/` 的文件；同一源版本可在多个档位之间复用本地输入，使用中的文件不会被缓存清理。缓存上限需覆盖原片和转码临时成品，超大录播应相应增大上限。执行租约超时、取消或源文件版本失效时终止工作，过期结果不能在主站发布。

## 发布

仓库根目录执行：

```powershell
pwsh ./release-worker.ps1 -Runtime win-x64 --version 1.2.3
pwsh ./release-worker.ps1 --all --version 1.2.3
```

支持 `linux-x64`、`linux-arm64`、`win-x64`、`osx-x64`、`osx-arm64`，默认 `linux-x64`。产物位于 `Artifacts/worker/<runtime>/`，Worker 发布为自包含 SingleFile 可执行文件，.NET 运行库与程序集包含在该文件内，原生库在首次启动时解压到运行账户可写的 bundle 目录。发布目录同时提供 `settings.example.json` 与说明，实际 `settings.json` 由运行时创建。FFmpeg、ffprobe 和 GPU 驱动由目标机器安装。任一平台发布失败立即停止。终端交互使用 Spectre.Console（MIT）。
