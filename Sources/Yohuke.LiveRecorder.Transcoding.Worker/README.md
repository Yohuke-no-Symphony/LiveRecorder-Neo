# 转码 Worker

独立的 .NET 10 命令行节点，通过 TCP＋TLS 接收主站任务，通过 HTTPS 下载原片并回传 HLS 成品。Worker 不需要 MongoDB，不提供入站端口，也不共享主站磁盘。

## 启动

在主站后台创建 Worker 并取得身份与密钥。将它们写入可执行文件旁的 `appsettings.json`：

```json
{
  "Worker": {
    "ApiBaseUrl": "https://recorder.example.com",
    "ControlHost": "recorder.example.com",
    "ControlPort": 4790,
    "WorkerId": "主站生成的节点身份",
    "Secret": "主站生成的节点密钥",
    "TlsServerName": null,
    "ServerCertificateSha256": null,
    "ControlCertificateSha256": null,
    "ApiCertificateSha256": null
  }
}
```

`ApiBaseUrl` 必须使用 HTTPS。主站需单独开启转码控制监听，并配置其 TLS 证书。默认端口为 4790；反向代理的 HTTPS 端口与 TCP 控制端口分别配置。系统可信证书不需要填写指纹；私有证书可填写其 DER 内容的 SHA-256 十六进制指纹，同时仍校验证书有效期与主机名。`TlsServerName` 只用于 TCP 连接的证书主机名；HTTPS 按 URL 的主机名验证。

`ControlCertificateSha256` 与 `ApiCertificateSha256` 可分别固定两个端点的证书；未设置时回退到共同的 `ServerCertificateSha256`。例如 HTTPS 反代使用系统可信证书而控制端使用私有证书，只需设置控制端指纹。

```sh
./Yohuke.LiveRecorder.Transcoding.Worker --config /path/to/appsettings.json
```

Windows 使用 `Yohuke.LiveRecorder.Transcoding.Worker.exe`。首次启动若主站尚未设置临时目录，会在交互终端进入配置向导；没有交互终端时请先在主站后台配置。FFmpeg、ffprobe、临时目录、编码并发、GPU 偏好、缓存上限和传输限速均由主站保存并下发，不能放入启动文件作为第二套运行配置。后台配置版本冲突时拒绝覆盖；重新进入向导后以主站最新配置为基础修改。

```sh
./Yohuke.LiveRecorder.Transcoding.Worker --configure
./Yohuke.LiveRecorder.Transcoding.Worker --check
```

`--configure` 修改主站运行配置后退出，`--check` 验证当前节点的 FFmpeg 编码能力并输出 JSON 后退出；两者也接受 `--config`。常驻模式使用 Ctrl+C 或 SIGTERM 安全停止，会等待当前 FFmpeg 进程树退出。密钥轮换后更新启动文件并重启 Worker。

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

支持 `linux-x64`、`linux-arm64`、`win-x64`、`osx-x64`、`osx-arm64`；默认 `linux-x64`。产物位于 `Artifacts/worker/<runtime>/`，包含自包含 .NET Worker、启动配置模板与说明。FFmpeg、ffprobe 和 GPU 驱动由目标机器安装；不会构建或打包 WebUI。任一平台发布失败立即停止。终端交互使用 Spectre.Console（MIT）。
