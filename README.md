# Yohuke.LiveRecorder

Bilibili 直播录制服务器：C# WebServer、MongoDB 与 Vue / Vuetify 媒体库。支持场次归档、文件弹幕、边录边看、后台切片、修复、合并和回收站。

[开发、运行、发布与测试说明](Sources/README.md)

服务端及前端位于 `Sources/`；共享 B站 API 位于 `Core/` 子模块。发布脚本位于根目录 `scripts/`，发布产物位于根目录 `Artifacts/<runtime>/`。

在仓库根目录使用 PowerShell 发布，默认目标为 `linux-x64`：

```powershell
pwsh ./release.ps1 --version 1.2.3
```

使用 `-Runtime win-x64` 可指定其他平台；使用 `--all` 一次发布五个平台：

```powershell
pwsh ./release.ps1 --all --version 1.2.3
```

独立转码节点使用 `pwsh ./release-worker.ps1 --all --version 1.2.3` 发布，产物位于 `Artifacts/worker/<runtime>/`。主站接入配置见[转码部署说明](Sources/README.md#分布式转码)，节点启动和 GPU 要求见 [Worker 说明](Sources/Yohuke.LiveRecorder.Transcoding.Worker/README.md)。

项目使用 [GPLv3](LICENSE)。FLV 组件固定源码来源及修改说明位于 `Sources/ThirdParty/BililiveRecorder.Flv/NOTICE`。
