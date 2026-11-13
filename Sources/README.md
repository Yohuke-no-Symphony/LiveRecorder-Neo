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

首次启动会输出内存中的安装令牌。打开页面后选择「全新安装」或「恢复实例」，填写令牌、管理员邮箱与密码、数据目录、录像目录及 MongoDB 连接。向导和校验阶段不创建 Data、录像目录、密钥或安装文件；验证通过并提交完成安装时才创建，不存在的目录会自动建立。安装直接在所选目录完成，无需重启。安装后在设置中配置 SMTP、邀请码、注册开关和 OAuth。

使用 `--config /持久目录/settings.json` 指定唯一设置文件；省略参数时读取可执行文件旁的 `settings.json`。找不到配置文件时进入未安装状态，成功安装或恢复后写入完整设置。开发时建议将该文件放在编译输出目录之外。

同一个 JSON 直接保存数据目录、录像目录和全部后台设置，例如：

```json
{
  "DataDirectory": "Data",
  "RecordsDirectory": "Data/records",
  "quality": 10000,
  "defaultLanguage": "zh-CN"
}
```

两条目录都以设置文件所在目录为相对路径基准，不受工作目录影响，也可以使用绝对路径。默认数据目录为该文件旁的 `Data/`，录像目录默认 `Data/records/`，也可放到其他磁盘。原 `--DataDirectory` 参数和 `LIVE_RECORDER_DATA_DIR` 环境变量已由此 JSON 取代。

数据目录只保存受保护的安装状态和数据库连接 `bootstrap.json`、安装令牌、`keys/` 密钥及 `cache/` 缓存。全局参数全部保存在可执行文件旁或 `--config` 选定的同一个 `settings.json`，不再新建 Data 内的第二份 settings；SMTP 密码和 OAuth 密钥仍受数据目录中的密钥保护。录像及其档案保存在独立的录像目录，媒体相对路径不包含主机上的绝对目录。

在后台「设置 → 常规 → 存储目录」可以修改两条目录并保存到同一个 settings.json，安装完成后的路径修改需重启生效，不会自动移动文件。重启前应停止录制并手动复制对应目录；迁移实例还需保留该设置文件。有效的安装状态会自动识别；设置文件丢失时需在安装页选择「恢复实例」。网站账号、B站账号、直播间和录像索引仍在 MongoDB 中，目标实例应连接原数据库，或同时恢复数据库备份。Windows 密钥使用运行账户的 DPAPI，恢复时需要保留对应账户的密钥环境。设置文件存在时，已安装实例的数据库故障、安装配置损坏或密钥缺失不会重新开放安装。

安装页的「全新安装」只接受空数据库；「恢复实例」要求原实例的有效管理员邮箱和密码，不会创建新管理员或重置配置。恢复优先使用唯一设置文件；旧实例的 Data/settings.json 或 MongoDB 全局设置可一次性导入该文件，之后不再读取第二份 settings。需要原密钥的凭据无法解密时会拒绝完成恢复，应补齐所选数据目录的 `keys/` 后重试。恢复模式的 FFmpeg、ffprobe 路径留空会沿用原设置，也可以填写目标机器上的新路径。

从旧版本升级时，先备份设置文件、数据目录、录像目录及 MongoDB。在唯一设置文件中填入两条真实目录，保持主播、场次、`.work/` 和 `.trash/` 内部结构不变，不必为独立录像目录重写数据库媒体索引。原 `.cache/` 可合并到数据目录的 `cache/`，也可重新生成。旧 `bootstrap.json`、`installation-token` 和全部 `keys/` 保留在数据目录。程序不会在启动时自动搬动已有录像。

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
./Yohuke.LiveRecorder.Server --urls http://127.0.0.1:4784 --config /var/lib/live-recorder/settings.json
```

反向代理应提供 HTTPS 并保留正确 Host。代理身份来源需要按部署环境配置可信代理，避免信任外部任意转发头。Linux 服务示例见 `deploy/live-recorder.service`。

## 账号

- 网站账号与 B站采集账号独立。B站扫码或短信登录由管理员操作，浏览器不提交 Cookie。
- 在「设置 → 注册」可独立控制管理员批准、邀请码和邮箱验证码，三项默认开启并兼容旧配置。关闭管理员批准后，新账号自动批准；关闭邀请码或邮箱验证码后，注册无需提供对应内容。普通注册与首次 OAuth 建号使用相同要求，注册完成均返回登录页；已绑定的 OAuth 账号仍按当前账号状态登录。
- OAuth 为 Authorization Code + S256 PKCE，配置授权、token、userinfo 端点及字段映射。Yohuke.UserService 可使用 `/oauth2/authorize`、`/oauth2/token`、`/oauth2/userinfo`，scope 为 `profile email`，稳定身份字段为 `sub`。
- 在提供方注册精确回调地址：`https://你的域名/oauth/callback/提供方标识`。
- 已有账号需要显式绑定外部身份，不能依用户名或邮箱自动合并。

在「录制管理 → 添加直播间」中，可通过「自动搜索」按昵称、UID 或房号查找主播并添加；关键词留空时显示所选 B站账号正在直播的关注列表，昵称搜索支持加载更多。「手动添加」接受标准房号和短号。两种方式共用录制账号和启用录制设置；切换账号后需重新搜索，已监控的搜索结果会标记为已添加。

## 界面语言

支持中文、English 和日本語，网站名分别为「夜半录播」「Yohuke Live Recorder」「夜更けのレコーダー」。用户可在「账户设置」选择界面语言，登录后跨设备保存；选择「跟随全局默认」时使用管理员在「设置 → 常规」中配置的语言，初始默认中文。访客可临时切换，选择保存在本机。

界面、系统提示及日期和数字格式随语言切换；直播标题、昵称、弹幕、礼物名和录播内容保留原文。

## 文件与媒体

```text
settings.json
Data/
  bootstrap.json
  installation-token
  keys/
  cache/
    artwork/
    playback/
  records/
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
