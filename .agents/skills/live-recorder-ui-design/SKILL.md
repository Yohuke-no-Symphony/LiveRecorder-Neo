---
name: live-recorder-ui-design
description: Add, update, or review cards, dialogs, forms, and page layouts in the Yohuke.LiveRecorder Vue and Vuetify WebUI. Reuse its compact styles and shared components while preserving interaction and accessibility. Applies to this WebUI only.
---

# Live Recorder UI Design

## Inspect the implementation

The WebUI lives in `Sources/WebUI`. Use paths relative to that directory if it is already the working directory. Read the target component and relevant shared code first.

- `src/plugins/vuetify.ts` defines semantic theme colors, corner radii, and defaults for buttons and form controls.
- `src/styles/main.css` defines shared spacing, page containers, grids, form rows, and toolbars.
- `src/components/common/{CardHeader,ContentCard,CardActions}.vue` defines the shared card and dialog structure.

Reuse existing feature components and Vue bindings. Extract repeated structures into `.vue` components. Preserve requests, permissions, form behavior, and close handlers when changing styles.

## Choose components

| Content | Component |
| --- | --- |
| Standard titled cards, settings cards, and form dialogs | Use `ContentCard` with the `title` prop. |
| Standard untitled content blocks | Use `ContentCard` and omit `title`. |
| Existing full-width tables, message lists, and media previews | Keep `v-card` and reuse `CardHeader` when needed. Keep the body flush with the card. |
| Cover cards, players, avatar identity cards, login, and first-time setup | Keep dedicated components and reuse the theme and shared spacing. |
| Specialized statistics and disk-usage cards | Keep the content structure and use `.card-content` for standard padding. |

In `ContentCard`, put the body in the default slot, brief details such as counts in `suffix`, header buttons in `header-actions`, and footer controls in `actions`. Use `titleId` to connect the surrounding dialog's `aria-labelledby` to the visible heading.

`CardHeader` has its own `actions` slot. Keep card headings as single-line H2s with ellipsis and the full text in the `title` attribute. Keep suffixes short and place longer details in the body. Use `PageHeader` for the page H1.

## Spacing and layout

Make shared spacing changes in `main.css` so pages inherit the same values.

| Variable or shared class | Convention |
| --- | --- |
| `--ui-card-padding` | 16px for standard body padding and horizontal alignment. |
| `--ui-card-header-padding` | 8px above and below the header and at the top of the body on titled cards. |
| `--ui-card-title-size` | 1rem card title size and a shared title weight of 600. |
| `--ui-form-gap` / `.form-stack` | 20px between form fields. |
| `--ui-layout-gap` | 20px for standard grid gaps. |
| `CardActions` | Right-aligned, wrapping controls with an 8px gap and 16px padding on the bottom and sides. |
| `.form-row` | Two columns, collapsing to one on narrow screens. |
| `.settings-grid` / `.summary-grid` | Two columns for settings and three for summaries, collapsing at existing breakpoints. |
| `.table-toolbar` / `.filter-bar` | Wrap controls and use shared spacing. |

Avoid adding `pa-5`, `pa-6`, or separate heading margins around `ContentCard`. Use `.section-heading` for secondary headings in the body.

Reuse `PageHeader` and `.page-container`. Let long headings, paths, and translated text shrink or wrap by setting `min-width: 0` on relevant flex or grid children. Use the existing 600px and 960px breakpoints for page layouts. Use local breakpoints when a control's content needs them. Size confirmation, search, and video dialogs for their content.

## Dialogs and forms

The caller owns the outer `v-dialog` model, width, `persistent` and `scrollable` behavior, lifecycle, and accessible name. Use `ContentCard` for the card structure.

For scrollable dialogs, keep the rendered `v-card-text` as a direct child of `v-card`. Place any `div` or `form` inside the body and keep the header and footer outside the scrolling area.

Keep standard forms in the body with `<form @submit.prevent>` and place their buttons in `<CardActions embedded>`. For a fixed footer, associate the submit button in the `actions` slot with a unique form ID.

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

Generate `formId` with Vue's `useId()` in the caller's setup. Get display strings from i18n or props. Keep native form submission to preserve Enter-key behavior and browser validation of required fields.

Preserve busy-state restrictions, drafts after failures, cancel-button states, and close callbacks. Check `updateOpen` in the search-and-add dialog, external form associations, `after-leave` cleanup after account login, and playback pausing when video previews close.

`CardActions` uses a plain `div` so callers retain their button styles. Keep it in place where `v-card-actions` would inject a default button variant and affect globally flat primary buttons. Use `primary` for the main action, the `text` variant for cancel, and `error` for destructive confirmations. Give icon buttons localized `aria-label` values.

## Copy and theme

Use supporting text only when it helps the user understand, enter information, or act. Remove copy that repeats field or button labels, restates obvious steps, exposes irrelevant implementation details, or fills space. State empty conditions directly. Keep counts and units on the title line when space allows, and show longer explanations where users need them.

Keep details that affect a decision, including what will be deleted, storage migration requiring a restart and a separate file move, the meaning of zero retention days, the scope of default permissions, SMTP certificate settings, and how amounts and unknown values are reported. Preserve errors, loading states, and input rules users cannot infer from the controls.

Use Vuetify semantic colors and `rgb(var(--v-theme-...))`. Follow global typography and card radii, and preserve specialized visuals such as media overlays. Update business copy in `zh-CN`, `en-US`, and `ja-JP` through localization.

## Verify and deliver

Run `pnpm type-check` and `pnpm build` from the WebUI directory. Select tests by the components affected.

```sh
pnpm test -- Tests/targetAddDialog.test.ts Tests/userPermissions.test.ts Tests/sessionLibrary.test.ts
```

When relevant, include `languageSettings`, `smtpSettings`, `analytics`, `storage`, `sessionTimeline`, `sessionDownloadDialog`, or `localization` tests. For spacing-only changes, keep verification focused on the affected UI.

Verify native submission, external form associations, close restrictions while busy, scrolling content with a fixed header and footer, long headings, narrow-screen actions, and readability in all three languages and both themes. Test observable behavior and clean up temporary visual and automation artifacts.

For substantial visual changes, ask the user to refresh and review screenshots of representative dialogs and narrow layouts. Report the checks that passed and any visual checks still pending.
