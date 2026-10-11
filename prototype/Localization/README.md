# Armor Control localization

English (`en-US`) is the default. Set `language = en-US` or `language = zh-CN` in the
server's `settings.cfg`. The native window and every connected browser share this
setting; old `armorControlLanguage` browser preferences are ignored.

Use either the in-game toolbar language buttons or the web header selector to save a
global choice on the KSP computer and synchronize connected pages through the existing WebSocket.
The browser sends `server.language.set` through the normal FIFO command queue; only a
successful settings save triggers the global update. Failed writes leave the old setting intact.
Joining and reconnecting clients
receive a reliable `server.settings` snapshot, even when no flight telemetry is changing.
The web selector changes the server setting, never an independent per-device preference.
Manual file edits are read on the next KSP startup; close KSP before editing the file.

## Contributing translations

- `en-US.json`: English source text and fallback translations.
- `zh-CN.json`: Simplified Chinese translations.
- `languages.json`: registered web languages and their display names.
- Use UTF-8 JSON. Every key and every English value must be English; values are strings.
- Keys are complete English source messages, not Chinese text or protocol identifiers.

For example, the English catalog contains:

```json
{
  "locale": "en-US",
  "messages": {
    "Activate cooling": "Activate cooling",
    "Stage {0}": "Stage {0}"
  }
}
```

The corresponding Chinese entries are:

```json
{
  "locale": "zh-CN",
  "messages": {
    "Activate cooling": "启动散热",
    "Stage {0}": "分级 {0}"
  }
}
```

Keep the same keys in both catalogs. Preserve every placeholder, including its name
and number of occurrences. `{0}` and `{name}` are supported; translations can reorder
them. Use complete sentences for changing values, not a collection of fragments.
Translations are plain text, never HTML.

The browser matches complete messages or anchored templates. It does **not** replace
substrings inside unrelated labels or names. The other language's catalog values can
also be recognized as source messages, for example a supported MechJeb status supplied
in the game's Chinese locale. JSON keys remain English in both cases. The catalogs
include ascent, landing, docking and rendezvous status messages from the supported
local MechJeb 2.14.3 localization files; this does not add support for newer MechJeb versions.

## Integrating UI text

Write owned HTML, JavaScript and C# display text in English, then add matching entries
to both catalogs. For explicit formatting, use `ArmorI18n.t('Stage {0}', {0: stage})`.
The browser also observes new text and label attributes written by telemetry updates.

Never translate input values, `data-*` values, protocol commands, enum names or IDs.
Prefer stable identifiers for logic. Where legacy controls must inspect a label,
use `ArmorI18n.sourceText(element)` rather than translated `textContent`.

Protect user/game names with `data-l10n-skip` on the **name itself**, not on a whole
card containing translatable controls. The frontend's `userText` helper protects a
real name but leaves an empty-state fallback localizable. Vessel, Kerbal, part and
resource names retain the game's language. Unknown third-party menu labels and
exception details retain their original wording until corresponding entries are added.

## Adding a language

Copy the English catalog, change `locale`, translate the values and register `code`
and `name` in `languages.json`. Do not rename the English keys. English is the built-in
fallback and must remain registered. Invalid catalogs or placeholder mismatches are
rejected; a failed catalog load keeps the current display language and shows an error.
Reconnection or a new server language update retries the load.

The native KSP window currently accepts `en-US` and `zh-CN` only. Adding another native
language also requires extending its C# validation, transport validation and language selector,
as well as the frontend's `followServerLanguage` locale allowlist.
Its dependency-free JSON reader accepts the catalog schema shown above: only `locale`
and `messages` at the root, with string keys and string values inside `messages`.
Keep contributor notes in this README, not additional JSON fields. Duplicate keys,
invalid escapes and incomplete catalogs are rejected with an English-source fallback.
Do not add serializer assemblies or serializer attributes to the game DLL: other KSP
mods scan type attributes, even for private types, before the localization loader runs.
Web catalogs are re-read on server language updates or reloading the page. Native
catalogs are cached for the current process; restart KSP to load edited native translations.

## Validation

From the ArmorControl repository root:

```powershell
node Tests/ArmorControl.LocalizationTests.cjs
node Tests/ArmorControl.ReviewRegression.cjs
./Tools/ArmorControl.VerifyRelease.ps1 -KspRoot 'C:\Games\KSP'
```

These repository test tools validate key parity, placeholders, default language,
live updates, reversible switching, protected names, unchanged command values,
failed/racing requests and native catalog loading. The full verification script
also checks the production DLL metadata. Test sources are tracked in Git but are
not shipped in the runtime release ZIP. Tests never launch or control KSP.

## 中文说明

语言由服务端 `settings.cfg` 统一决定，默认英文，浏览器的旧选择不再生效。所有词条键必须使用完整的英文原文，
中文写在 `zh-CN.json` 的值中。动态文案使用完整句子和占位符，两份词典保持相同的键
以及占位符。不要使用中文键、片段替换或翻译协议值；不要为了保护零件名而跳过整个
操作卡片。修改翻译后请执行上述检查，并验证中英文切换及实时状态更新。
