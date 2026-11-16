<p align="center">
  <img src="Sources/WebUI/public/branding/yohuke-logo.png" width="112" alt="夜半录播图标" />
</p>

<h1 align="center">Yohuke Live Recorder</h1>

<p align="center">夜半录播 · B 站直播录制整理、数据复盘与切片</p>

<p align="center">
  <a href="#快速部署">快速部署</a> ·
  <a href="#功能">功能介绍</a> ·
  <a href="Sources/README.md">开发文档</a> ·
  <a href="Sources/Yohuke.LiveRecorder.Transcoding.Worker/README.md">转码节点</a>
</p>

## 功能

- **直播录制**：监控开播、断线重连，多 B 站账号自动替补，保存视频与弹幕事件。
- **录播整理**：按主播和场次浏览，支持归组、拆分、修复、合并、回收站和自动清理。
- **在线回放**：边录边看、弹幕回放、清晰度切换、原片下载与切片导出。
- **分布式转码**：独立 Worker 支持 GPU 编码和多清晰度 HLS，保留原画。
- **直播记录**：导入 Danmu Neo 的 YAML 记录，校准时间，点击记录跳转播放。
- **统计分析**：查看弹幕、SC、礼物、大航海和在线人数，按时间筛选并导出 Excel。
- **AI 复盘**：接入支持工具调用的模型，根据弹幕和 SC 生成话题总结与直播记录。
- **账号与权限**：网站账号与 B 站账号分开管理，支持邮箱注册、密码找回、OAuth 和用户权限；界面支持中文、英文和日文。

## WebUI
本项目支持现代化的WebUI查看录像、管理服务器。

### 录播库
> 直观便捷的媒体库式录像管理

![录播库](Screenshots/library.webp)

### 录像回放
> 直播数据全记录，添加编码worker更可以流畅回放正常直播，支持时间范围切片取回

![录像回放](Screenshots/playback.webp)

### 直播分析
> 直播运营工具，复盘数据轻松浏览，更支持excel导出，轻松处理数据

![直播分析](Screenshots/analytics.webp)
![直播分析](Screenshots/analytics2.webp)
![直播分析](Screenshots/analytics3.webp)
![直播分析](Screenshots/analytics4.webp)

### 账号管理
> 多录像账号批量管理，轻松登陆，账号下线自动替补，保证录播稳定

![账号管理](Screenshots/account.webp)
![账号管理](Screenshots/account2.webp)

### Streamer
> 主播账号轻松搜索关联，一键启用/停用

![Streamer](Screenshots/streamer.webp)
![Streamer](Screenshots/streamer2.webp)

## 快速部署

支持 Linux、Windows 和 macOS。发布包自带 .NET 运行时，运行时不需要 Node.js。

1. 准备 MongoDB 8.x、FFmpeg 和 ffprobe，将媒体工具加入 `PATH`，或在安装向导中填写绝对路径。
2. 从 [Releases](https://github.com/Yohuke-no-Symphony/LiveRecorder-Neo/releases/latest) 下载 `server-*.zip` 并解压。未提供的平台可按[开发文档](Sources/README.md#发布)自行构建。
3. 在解压目录启动服务：

   ```sh
   chmod +x ./Yohuke.LiveRecorder.Server
   ./Yohuke.LiveRecorder.Server --urls http://0.0.0.0:4784 --config ./settings.json
   ```

   Windows 跳过 `chmod`，使用 `.\Yohuke.LiveRecorder.Server.exe`，参数相同。

4. 访问 `http://服务器地址:4784`，填写终端输出的安装令牌，按向导创建管理员、选择数据与录像目录、连接 MongoDB。登录后添加 B 站账号和直播间，开启录制。

对外使用时配置 HTTPS 反向代理；代理设置、备份和迁移见[部署与配置](Sources/README.md#部署与配置)。


## 许可证

[GPL-3.0](LICENSE)。第三方 FLV 组件说明见 [NOTICE](Sources/ThirdParty/BililiveRecorder.Flv/NOTICE)。
