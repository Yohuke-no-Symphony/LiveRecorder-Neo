# AI Worker

AI 节点执行弹幕与 SC 复盘、AI 直播记录及服务商连接测试。主站统一保存服务商、模型和执行限制；节点运行时领取凭据、下载固定证据来源，在节点整理统计和上下文，完成后回传结果，由主站发布直播记录。

节点不需要 MongoDB、FFmpeg 或本地模型。发布包包含 .NET 运行时。每个节点默认同时执行 1 个任务，主站还控制全局并发。

## 部署

从 [Releases](https://github.com/Yohuke-no-Symphony/LiveRecorder-Neo/releases/latest) 下载对应平台的 `workers-<版本>-<平台>.zip`，解压后进入 `ai/`。在主站「管理 → Worker → AI」创建节点，保存节点 ID 和访问密钥。在程序目录执行：

```sh
chmod +x ./ylr-ai-worker
./ylr-ai-worker register
./ylr-ai-worker run
```

Windows 使用 `.\ylr-ai-worker.exe`。注册向导配置主站 HTTP 地址、TCP 控制地址、TLS、节点凭据和临时目录。HTTP 与 TCP 控制通道分别配置；TCP 默认端口为 `4790`，需要可达并与主站的 TLS 设置一致。

也可以复制 `settings.example.json`，填写节点凭据，并将 `Configuration.TempDirectory` 改为可写的绝对路径。`Worker.Secret` 是节点访问凭据；服务商 API key 不写入节点配置、检查点或日志。

```sh
./ylr-ai-worker register --config /absolute/path/settings.json
./ylr-ai-worker config --config /absolute/path/settings.json
./ylr-ai-worker run --config /absolute/path/settings.json
```

`config` 修改缓存目录、任务并发、缓存上限、最低空闲空间和上传下载限速，随后与主站同步版本。运行中由主站下发资源配置。并发范围为 1–64，默认 1；限速单位为字节/秒，`0` 表示不限速。更改本机配置前先停止服务；版本冲突时运行 `config` 重新读取主站配置。

## 检查点与恢复

任务目录位于 `<TempDirectory>/live-recorder-ai-worker/<节点身份哈希>/jobs/<执行身份哈希>/`。其中保存固定输入、精简证据、私有上下文、待确认请求包和模型响应日志。配置旁的 `.ai-checkpoints.json` 登记历史缓存根目录，移动临时目录后仍能发现未完成任务。Unix 下敏感缓存文件仅允许运行账户读写。

模型请求前必须先由主站确认本轮在途检查点，再原子记录已发送标记。节点重启后先重新确认任务所有权；确定未发送的请求可恢复执行。已经发送而结果未知的请求不会自动重发，需要在主站查看中断状态并决定后续处理。

已持久化的响应可以恢复本轮纯工具处理和结果回传，不会重复调用服务商。断连后可继续处理这份已知响应，任何下一轮模型请求都需要有效租约与新的主站确认。主站仅授权补交时，节点完成当前响应的处理后停止，不新增计费调用。取消或被其它执行代次取代的任务不能凭本地缓存重新启动。

检查点与最终结果使用严格递增序号；确认丢失时重传同一原包。缓存预算不足时优先回收已完成或失效的任务，保留未完成及结果未知的检查点；空间仍不足时停止推进，等待释放空间或更改配置。请保留配置、目录登记文件和缓存，直到主站确认结果。

## 系统服务

使用固定程序与配置目录，在管理员终端运行（Windows 移除 `sudo`）：

```sh
sudo ./ylr-ai-worker install --config /absolute/path/settings.json
sudo ./ylr-ai-worker enable
sudo ./ylr-ai-worker start
```

安装后默认停止且不开机启动。`start` / `stop` 控制运行，`enable` / `disable` 控制开机启动，`status` / `log` 查看状态与日志，`uninstall` 移除服务。升级时先停止服务，再替换程序并启动。Unix 服务使用 root，Windows 使用 LocalSystem，需要相应账户可写的配置和缓存目录。

Linux 使用 systemd，Windows 使用 Windows Service，服务名为 `yohuke-live-recorder-ai-worker`；macOS 使用 LaunchDaemon，标签为 `com.yohuke.live-recorder.ai-worker`，定义与日志位于 `/Library/Application Support/Yohuke.LiveRecorder/AiWorkerService`。服务意外退出后的恢复遵循共用 Worker 服务策略。

## 源码验证

在仓库根目录使用 .NET 10 SDK：

```sh
dotnet test Sources/Tests/AIWorker/Yohuke.LiveRecorder.AI.Worker.Tests.csproj
dotnet publish Sources/Worker/Yohuke.LiveRecorder.AI.Worker/Yohuke.LiveRecorder.AI.Worker.csproj -c Release -r linux-x64
```

统一发布使用 `pwsh ./release-worker.ps1 -Runtime linux-x64 --version 1.2.3`，AI 程序、配置示例、部署说明和许可证位于 Worker ZIP 的 `ai/` 目录。

`help` 可查看全部 CLI 命令，`version` 显示节点版本。节点日志只记录安全错误标识或异常类型，不记录模型正文和服务商凭据。
