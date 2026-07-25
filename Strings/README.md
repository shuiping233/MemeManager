English | [简体中文]

# Adding a New Language

This app discovers supported languages automatically at runtime by scanning the
`Strings/` directory. Each subfolder named like a culture code (e.g. `ja`, `fr-FR`)
that contains a `Resources.resw` file becomes an available language in the
settings dropdown — **no code changes required**.

## Steps

1. Copy an existing language folder, e.g. `Strings/en-US/`, into a new folder
   named after the culture code you want to add:
   - `Strings/<culture-code>/Resources.resw`
   - Example: `Strings/ja/Resources.resw`

2. Translate the string values inside the new `Resources.resw`. The `name`
   attributes (resource keys) must stay identical to the other languages.

3. That's it. The language appears automatically in
   `Settings → Language` after a rebuild.

## IMPORTANT: the `Settings_Language_*` keys

The language dropdown itself is built from these keys:

- `Settings_Language_System` — the "Follow system" item (one per language file).
- `Settings_Language_<culture-code>` — the label for each specific language.

For a new language folder `Strings/ja/`, you MUST include:

```xml
<data name="Settings_Language_ja" xml:space="preserve">
    <value>日本語</value>
</data>
```

and the `Settings_Language_System` key as well.

### Critical cross-file rule

When you add a new language `Strings/xxx/`, you must ALSO add a
`Settings_Language_xxx` entry to **every other** `Resources.resw` file
(`zh-CN`, `en-US`, `ja`, ...).

Reason: the dropdown lists all discovered languages, and each item's label is
resolved in the *currently active* language. If `Strings/en-US/Resources.resw`
is missing `Settings_Language_ja`, then while the app is running in English the
"日本語" dropdown item will show up **blank**.

So for every new language `xxx`, add to all existing language files:

```xml
<data name="Settings_Language_xxx" xml:space="preserve">
    <value><display name of xxx in this language></value>
</data>
```

If a `Settings_Language_<code>` key is missing in the active language, the app
falls back to the culture's native name (via `CultureInfo`), so it won't crash —
but adding the key keeps the wording consistent and intentional.

## Notes

- Only subfolders that contain a `Resources.resw` are registered; empty folders
  are ignored.
- `DefaultLanguage` falls back to `zh-CN` if present, otherwise the first
  discovered language.
- The language code must be a valid .NET culture name understood by
  `CultureInfo` (used for the fallback display name). See the Microsoft
  locale/culture reference for valid codes:
  https://learn.microsoft.com/zh-cn/openspecs/windows_protocols/ms-lcid/
  or use the following PowerShell command to list culture names
  ```powershell
  [System.Globalization.CultureInfo]::GetCultures("AllCultures") | Where-Object { -not $_.IsNeutralCulture } | Select-Object Name, NativeName | Where-Object Name
  ```



[简体中文]: ./README.zh-CN.md