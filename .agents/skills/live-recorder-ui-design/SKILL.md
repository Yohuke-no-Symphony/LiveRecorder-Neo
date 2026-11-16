---
name: live-recorder-ui-design
description: 为 Yohuke.LiveRecorder 的 Vue 与 Vuetify WebUI 新增、修改或审查卡片、弹窗、表单和页面布局，沿用项目紧凑样式与共用组件，保留交互和可访问性。适用于该项目 UI 开发与样式统一，不用于 Unity、Avalonia 或其它项目的界面设计。
---

# Live Recorder UI 设计规范

## 读取真实实现

项目入口为仓库内的 `Sources/WebUI`；工作目录已经是 WebUI 时使用相对路径。先读取目标组件及相关来源，以实际代码为准：

- `src/plugins/vuetify.ts`：主题语义色、圆角、按钮与表单控件默认外观。
- `src/styles/main.css`：公共间距、页面容器、网格、表单行和工具栏。
- `src/components/common/{CardHeader,ContentCard,CardActions}.vue`：卡片与弹窗的公共结构。

复用已有业务组件与 Vue 数据绑定；同类内容封装为 `.vue` 组件。样式修改保留现有请求、权限、表单和关闭逻辑，避免扩展为业务重构。

## 组件选择

| 内容 | 使用方式 |
| --- | --- |
| 有标题的普通内容卡、设置卡、表单弹窗 | `ContentCard`，标题传 `title` |
| 无标题的普通内容块 | `ContentCard`，省略 `title` |
| 已有全宽表格、消息列表、媒体预览 | 保留 `v-card`，需要标题时复用 `CardHeader`，避免正文再套一层内边距 |
| 封面卡、播放器、头像身份卡、登录及首次安装页 | 保留专用组件，复用主题和公共间距 |
| 统计数字、磁盘等专用卡片 | 保留内容结构，常规内距用 `.card-content` |

`ContentCard` 的默认插槽放正文，`suffix` 放计数等短信息，`header-actions` 放标题栏按钮，`actions` 放底部操作。`titleId` 将外层弹窗的 `aria-labelledby` 关联到实际标题。

`CardHeader` 自身的操作插槽名为 `actions`。标题使用 H2、单行省略和完整 `title` 属性；后缀保持简短，长业务信息放正文。页面 H1 仍使用 `PageHeader`。

## 间距与布局

统一调整时修改 `main.css` 的公共变量，避免各页面复制一套数值：

| 变量或公共类 | 当前约定 |
| --- | --- |
| `--ui-card-padding` | 常规正文内距及左右对齐 16px |
| `--ui-card-header-padding` | 标题上下及有标题正文顶部 8px |
| `--ui-card-title-size` | 卡片标题 1rem，公共标题字重 600 |
| `--ui-form-gap` / `.form-stack` | 字段间距 20px |
| `--ui-layout-gap` | 常规网格间距 20px |
| `CardActions` | 右对齐、8px 间距、允许换行，底部与左右 16px |
| `.form-row` | 两列字段，窄屏改单列 |
| `.settings-grid` / `.summary-grid` | 设置两列、统计三列，按现有断点折叠 |
| `.table-toolbar` / `.filter-bar` | 允许换行，沿用公共间距 |

不要在 `ContentCard` 外叠加 `pa-5`、`pa-6` 或独立标题底距。正文中的次级标题用 `.section-heading`。

页面复用 `PageHeader` 和 `.page-container`。长标题、路径和翻译文本要能收缩或换行，相关 flex/grid 子项使用 `min-width: 0`。主要页面断点沿用 600px 和 960px，特殊控件可按内容使用局部断点；确认、搜索和视频弹窗按内容保留各自宽度。

## 弹窗与表单

外层 `v-dialog` 由调用方控制模型、宽度、`persistent`、`scrollable`、生命周期和 ARIA 名称。`ContentCard` 只封装卡片结构。

可滚动弹窗必须保持渲染后的 `v-card-text` 为 `v-card` 直接子元素；不要在两者间再包 `div` 或 `form`。标题和底部操作保持在滚动正文之外。

普通表单在正文内保留 `<form @submit.prevent>`，按钮放在 `<CardActions embedded>` 内。需要固定底部操作时，用唯一表单 ID 关联 `actions` 插槽中的提交按钮：

```vue
<v-dialog v-model="opened" max-width="580" scrollable :persistent="busy" :aria-label="title">
  <ContentCard :title="title">
    <form :id="formId" class="form-stack" @submit.prevent="save">
      <v-text-field v-model="name" :label="nameLabel" required :disabled="busy" />
      <v-alert v-if="error" type="error">{{ error }}</v-alert>
    </form>
    <template #actions>
      <v-btn variant="text" :disabled="busy" @click="opened = false">{{ cancelLabel }}</v-btn>
      <v-btn type="submit" :form="formId" color="primary" :loading="busy">{{ saveLabel }}</v-btn>
    </template>
  </ContentCard>
</v-dialog>
```

在调用方 setup 中用 Vue `useId()` 生成 `formId`；展示字符串来自 i18n 或 props。不要用 `@click="save"` 替代原生 submit，导致 Enter 和浏览器 required 校验丢失。

保留现有忙碌限制、失败后的草稿、取消按钮状态与关闭回调，尤其是搜索添加的 `updateOpen`、外置 form 关联、账号登录的 `after-leave` 清理和视频预览关闭时暂停播放。

`CardActions` 用普通 div，保留调用方按钮样式。不要随意替换为 `v-card-actions`，Vuetify 会为子按钮注入默认 variant，可能改变全局 flat 主按钮。主操作使用 primary，取消用 text，破坏性确认使用 error；图标按钮提供本地化 `aria-label`。

## 文案与主题

**禁止添加无用的说明小字。** 不得重复字段或按钮含义、复述显而易见的流程、展示用户无需了解的内部实现细节，或为填充布局添加提示。仅在确实有助于用户理解、输入或操作时添加说明，删除已有冗余说明。空态直接说明当前状态。计数与统计单位尽量放标题同行，长解释按需展示。

保留影响决策的说明：确认对象和删除范围、目录迁移不移动文件且需重启、保留天数为零、默认权限影响范围、SMTP 证书设置含义、金额和未知值口径。不要为了压缩高度删除错误、加载状态或非直观输入规则。

主题色使用 Vuetify 语义色及 `rgb(var(--v-theme-...))`；字体与卡片圆角沿用全局设置，媒体覆盖层等特殊视觉可保留。文案同步 `zh-CN`、`en-US`、`ja-JP`，不在组件中写死业务文字。

## 验证与交付

从 WebUI 运行 `pnpm type-check` 与 `pnpm build`。按受影响组件筛选测试，例如：

```sh
pnpm test -- Tests/targetAddDialog.test.ts Tests/userPermissions.test.ts Tests/sessionLibrary.test.ts
```

按修改范围补充 `languageSettings`、`smtpSettings`、`analytics`、`storage`、`sessionTimeline`、`sessionDownloadDialog` 或 `localization` 测试；纯间距改动不自动运行全部后端测试。

验证可观察行为：原生提交、外置 form 关联、忙碌时关闭限制、滚动正文与固定头尾、长标题及窄屏操作区，以及三语和明暗主题下的可读性。不用匹配 padding 字符串的测试代替行为验收；完成后清理临时视觉和自动化验收文件。

视觉改动较多时，请用户对刷新后的代表性弹窗和窄屏布局截图验收。交付时区分已通过的自动化检查和仍待人工验收的部分。
