# Yohuke.LiveRecorder

Bilibili 直播录制服务器，使用 .NET 10、MongoDB 与 Vue / Vuetify。按主播与直播场次组织录像，支持边录边看、文件弹幕、后台切片、修复、合并及可恢复删除。

## 运行环境

- Linux 优先；支持 Windows、macOS 原生运行。
- MongoDB 8.x，支持单节点；使用独立数据库。
- FFmpeg 与 ffprobe，需要 H.264/AAC 编解码能力。媒体测试已验证 FFmpeg 9.0.2。
- 开发需要 .NET 10 SDK、Node.js 22.12+ 与 pnpm。

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

首次启动会输出内存中的安装令牌。安装向导第一步选择「全新安装」或「恢复实例」并填写令牌；全新安装随后填写管理员信息，恢复实例跳过管理员步骤，两种方式再依次填写数据目录、录像目录及 MongoDB 连接。向导和校验阶段不创建 Data、录像目录、密钥或安装文件；验证通过并提交完成安装时才创建，不存在的目录会自动建立。安装直接在所选目录完成，无需重启。安装后在设置中配置 SMTP、邀请码、注册开关和 OAuth。

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

安装页的「全新安装」只接受空数据库；「恢复实例」使用安装令牌授权，无需填写管理员信息，原数据库中必须存在可用的管理员账号。恢复会保留原账号与密码，完成后使用原账号登录。恢复优先使用唯一设置文件；旧实例的 Data/settings.json 或 MongoDB 全局设置可一次性导入该文件，之后不再读取第二份 settings。需要原密钥的凭据无法解密时会拒绝完成恢复，应补齐所选数据目录的 `keys/` 后重试。恢复模式的 FFmpeg、ffprobe 路径留空会沿用原设置，也可以填写目标机器上的新路径。

从旧版本升级时，先备份设置文件、数据目录、录像目录及 MongoDB。在唯一设置文件中填入两条真实目录，保持主播、场次、`.work/` 和 `.trash/` 内部结构不变，不必为独立录像目录重写数据库媒体索引。原 `.cache/` 可合并到数据目录的 `cache/`，也可重新生成。旧 `bootstrap.json`、`installation-token` 和全部 `keys/` 保留在数据目录。程序不会在启动时自动搬动已有录像。

## 发布

以下命令在仓库根目录执行。PowerShell 入口默认发布 `linux-x64`，使用 `-Runtime` 指定单个平台，或使用 `--all` 一次发布全部五个平台：

```powershell
pwsh ./release.ps1 --version 1.2.3
pwsh ./release.ps1 -Runtime win-x64 --version 1.2.3
pwsh ./release.ps1 --all --version 1.2.3
```

`--version` 可省略，未指定时沿用项目默认版本；指定时仅设置本次发布版本，不修改项目文件。批量发布只在第一个平台构建前端，后续平台复用构建结果；任一平台失败时立即终止。

也可使用 Bash 脚本发布单个平台：

```sh
./scripts/publish.sh linux-x64
```

支持 `linux-x64`、`linux-arm64`、`win-x64`、`osx-x64`、`osx-arm64`。发布产物在仓库根目录的 `Artifacts/<runtime>/`，包括自包含服务端和 `webui/`。FFmpeg 与 ffprobe 由目标机器安装，也可以在安装页和管理设置中填写绝对路径。

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

在「设置 → 媒体与存储」可分别设置录像自动删除周期和回收站自动彻底删除周期，单位为天，0 表示关闭。录像自动删除默认关闭；启用后，从录像结束或回收站恢复时起计算，到期先进入回收站。永久保留录像跳过自动清理，仍可手动删除。拆分继承永久保留标记，归组时任一来源永久保留则归组录像也永久保留。回收站默认 7 天彻底删除，周期变更也应用于已有录像和切片回收记录。

播放页右侧可切换「弹幕」和「直播记录」。在直播记录中上传 DanmuNeo 原生 `.yaml` / `.yml` 文件，同一场录像可保存多份文件，并按文件名、上传者和上传时间选择。每份文件最大 5 MiB、20,000 条；记录文字和七色标识保持原样，点击记录可跳转到录像的对应时间，超出录像范围的记录保留显示但不能跳转。

记录按每条的实际时间对齐录像，支持文件级整体校准：正秒数向录像后方移动，负秒数向前移动，不修改上传的原文。旧文件中的日期没有时区时，上传时选择来源时区，默认使用站点时区；之后修改站点时区不会改变已上传记录的时间。文件向所有能观看该场录像的成员共享，与个人标记分开。上传者、有直播记录管理权限的成员和管理员可校准或永久删除该份文件，删除不进入回收站，也不影响其他录像中的独立副本。

直播记录附件随整场录像回收和恢复，彻底清除场次时一起清理。归组、拆分会保留独立副本和原上传者；拆分后的两侧保留完整记录，超出各自范围的条目不能跳转。再次归组时，仅合并同源、内容及校准完全相同的副本。

管理员可在「用户与邀请」按录播、直播记录和分析分组授予权限，包括观看录像、下载录像、创建切片、上传直播记录、管理他人直播记录，以及原有的删除录像与设置永久保留、查看分析数据、彻底删除录像。录像管理权限包括恢复共享录像，彻底删除权限仅允许从回收站删除；个人切片仍仅对创建者和管理员开放。下载权限控制原始录像、个人切片和作业产物的下载入口，播放和创建切片等操作仍需观看权限。

普通用户默认拥有观看、下载、创建切片和上传记录四项权限，其他权限初始关闭；旧账号升级保留原有授权并补齐这四项能力。「设置 → 注册」可调整之后注册账号的默认权限，不会覆盖现有用户或重新补回已经明确撤销的权限。管理员始终拥有全部权限，授权或撤权在已登录用户的下一次请求生效，包括已经取得的播放资源地址和任务重试；已接受的后台任务继续运行。

录制优先 AVC/AAC。上游非标准 codec-12 HEVC 能力仍需真实样本验证；增强 FLV 会明确拒绝，不能假装修复成功。原始录制不会被离线修复覆盖。

观测金额由金币面值与协议支付组成，报价单列且不计总额；未知价格和采集缺口单独记录。它不代表用户实付或主播结算，活跃用户也仅包含观察到互动的 UID。

直播分析按当前账号的个人同传正则识别弹幕，未设置个人规则时沿用全局规则。同传弹幕从弹幕数、发言人数、互动用户、付费参与率分母、互动曲线和活跃榜中剔除；同传者发送的普通弹幕、SC、礼物和大航海仍正常统计。独立的同传板块展示同传总条数、同传者人数、每分钟合计量和个人条数前 100 名，头像与用户名可打开 B站主页。无法识别发送者的同传只计入总量与分钟图表。规则修改后会立即重建统计，无需重新录制；原始弹幕归档不变。

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
