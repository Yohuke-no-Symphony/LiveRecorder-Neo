# Yohuke.LiveRecorder

Bilibili 直播录制服务器，使用 .NET 10、MongoDB 与 Vue / Vuetify。按主播与直播场次组织录像，支持边录边看、文件弹幕、后台切片、修复、合并及可恢复删除。

## 运行环境

- Linux 优先；支持 Windows、macOS 原生运行。
- MongoDB 8.x，支持单节点；使用独立数据库。
- FFmpeg 与 ffprobe，需要 H.264/AAC 编解码能力。媒体测试已验证 FFmpeg 9.0.2。
- 开发需要 .NET 10 SDK、Node.js 22.12+ 与 package.json 指定的 pnpm 12.9.1。

## 开发

```sh
dotnet run --project Yohuke.LiveRecorder.Server -- --urls http://127.0.0.1:4784
```

```sh
cd WebUI
pnpm install --frozen-lockfile
pnpm dev
```

开发前端为 `http://127.0.0.1:5784`。`src/apis/ServerURL.ts` 的 `getServerUrl(path)` 在开发时读取 `VITE_API_BASE_URL`，默认直接访问后端 `http://127.0.0.1:4784`；发布构建使用 `window.location.origin`。API、封面、下载及播放地址共用此入口。

修改调试后端地址时，在 `WebUI/.env.development.local` 中设置并重启 `pnpm dev`：

```dotenv
VITE_API_BASE_URL=http://127.0.0.1:4884
```

后端使用 `Development` 环境启动，允许 `appsettings.Development.json` 中 `WebUI:DevelopmentOrigins` 指定的前端携带 Cookie 与 CSRF 令牌访问。更换前端端口时同步修改该列表。前后端使用相同主机名与协议，例如均使用 `http://127.0.0.1`，保证登录 Cookie 可用。开发时 OAuth 回调到前端端口，提供方应登记 `http://127.0.0.1:5784/oauth/callback/提供方标识`。

首次启动会输出安装令牌。打开页面后填写令牌、管理员邮箱与密码、录播目录及 MongoDB 连接。安装后在设置中配置 SMTP、邀请码、注册开关和 OAuth。

`LIVE_RECORDER_DATA_DIR` 或 `--DataDirectory` 指定服务数据目录。默认位于程序旁的 `data/`，保存保护后的安装配置和 Data Protection 密钥。备份时需要同时保存该目录、MongoDB 与录播目录。Windows 密钥使用运行账户的 DPAPI，恢复时需要保留对应账户的密钥环境。已安装实例的数据库故障不会重新开放安装。

## 发布

```sh
./scripts/publish.sh linux-x64
```

Windows PowerShell：

```powershell
./scripts/publish.ps1 -Runtime win-x64
```

支持 `linux-x64`、`linux-arm64`、`win-x64`、`osx-x64`、`osx-arm64`。发布产物在 `artifacts/<runtime>/`，包括自包含服务端和 `webui/`。FFmpeg 与 ffprobe 由目标机器安装，也可以在安装页和管理设置中填写绝对路径。

运行发布产物：

```sh
./Yohuke.LiveRecorder.Server --urls http://127.0.0.1:4784 --DataDirectory /var/lib/live-recorder
```

反向代理应提供 HTTPS 并保留正确 Host。代理身份来源需要按部署环境配置可信代理，避免信任外部任意转发头。Linux 服务示例见 `deploy/live-recorder.service`。

## 账号

- 网站账号与 B站采集账号独立。B站扫码或短信登录由管理员操作，浏览器不提交 Cookie。
- 本站注册必须同时通过邮件验证码、邀请码和管理员审核。首次 OAuth 建号执行相同条件。
- OAuth 为 Authorization Code + S256 PKCE，配置授权、token、userinfo 端点及字段映射。Yohuke.UserService 可使用 `/oauth2/authorize`、`/oauth2/token`、`/oauth2/userinfo`，scope 为 `profile email`，稳定身份字段为 `sub`。
- 在提供方注册精确回调地址：`https://你的域名/oauth/callback/提供方标识`。
- 已有账号需要显式绑定外部身份，不能依用户名或邮箱自动合并。

## 文件与媒体

```text
录播根/
  主播名称_UID/
    主播信息.json
    日期_时间_直播标题_场次短ID/
      场次.json
      录像/
      弹幕/事件.jsonl
      弹幕/事件.index.jsonl
      分析/
      切片/
      修复/
      合并/
  .cache/
  .work/
  .trash/
```

目录与文件名保留中文；管理索引使用稳定 ID。逐条事件、文件侧索引和用户互动分析不写 MongoDB。

同场断流保留真实时间：播放空档显示提示，弹幕按实际时间回放；精确导出包含等时长占位画面。快速切片只适用于无空档且媒体参数兼容的范围，界面显示实际边界。

切片完成后默认保留 30 天，删除或到期后进入回收站，默认再保留 7 天；期限可在设置中调整。切片只对创建者和管理员开放。恢复使用当前保留期重新计时。

录制优先 AVC/AAC。上游非标准 codec-12 HEVC 能力仍需真实样本验证；增强 FLV 会明确拒绝，不能假装修复成功。原始录制不会被离线修复覆盖。

观测金额由金币面值与协议支付组成，报价单列且不计总额；未知价格和采集缺口单独记录。它不代表用户实付或主播结算，活跃用户也仅包含观察到互动的 UID。

## 测试

```sh
dotnet test Tests/Server/Yohuke.LiveRecorder.Tests.csproj
dotnet test Tests/Media/Yohuke.LiveRecorder.Media.Tests.csproj
```

MongoDB 集成测试使用独立实例与每次随机数据库名，配置：

```sh
export LIVE_RECORDER_TEST_MONGO=mongodb://127.0.0.1:27017
dotnet test Tests/Identity/Yohuke.LiveRecorder.Identity.Tests.csproj
dotnet test Tests/Http/Yohuke.LiveRecorder.Http.Tests.csproj
dotnet test Tests/Grouping/Yohuke.LiveRecorder.Grouping.Tests.csproj
dotnet test Tests/Recording/Yohuke.LiveRecorder.Recording.Tests.csproj --filter 'FullyQualifiedName!~RecordingPressureTests'
```

32 路持续采集、4 路播放窗口及后台切片压力验收：

```sh
LIVE_RECORDER_STRESS=1 dotnet test Tests/Recording/Yohuke.LiveRecorder.Recording.Tests.csproj --filter FullyQualifiedName~RecordingPressureTests
```

```sh
cd WebUI
pnpm test
pnpm build
```

真实 B站扫码、手机号、极验和安全中心需使用管理员自己的账号完成验收。自动测试不会向真人发送邮件或短信。

## 许可证

GPL-3.0。FLV 组件固定来源、修改记录和依赖说明见 `ThirdParty/BililiveRecorder.Flv/NOTICE`；B站 API 保留在 Core 子模块中。
