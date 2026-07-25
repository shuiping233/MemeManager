[English] | 简体中文

# 新增语言说明

本程序在运行时会自动扫描 `Strings/` 目录来发现支持的语言。每个以语言代码命名
（如 `ja`、`fr-FR`）且包含 `Resources.resw` 文件的子文件夹，都会自动成为设置里
语言下拉菜单中的一个选项——**无需修改任何代码**。

## 步骤

1. 复制一个已有的语言文件夹，例如 `Strings/en-US/`，粘贴为以目标语言代码命名的
   新文件夹：
   - `Strings/<语言代码>/Resources.resw`
   - 例如：`Strings/ja/Resources.resw`

2. 翻译新 `Resources.resw` 里的字符串值。`name`（资源 key）必须与其他语言保持
   完全一致，只改 `<value>`。

3. 完成。重新编译后，语言下拉菜单里会自动出现该语言。

## 重要：`Settings_Language_*` 这一组 key

语言下拉菜单本身是由以下 key 构建的：

- `Settings_Language_System` —— “跟随系统”那一项（每个语言文件都要有）。
- `Settings_Language_<语言代码>` —— 每一种具体语言的显示名。

对于新语言文件夹 `Strings/ja/`，必须包含：

```xml
<data name="Settings_Language_ja" xml:space="preserve">
    <value>日本語</value>
</data>
```

同时别忘了 `Settings_Language_System` 这一条。

### 关键的多文件联动规则（务必遵守）

当你新增一种语言 `Strings/xxx/` 时，必须**同时**在**所有其他** `Resources.resw`
文件（`zh-CN`、`en-US`、`ja`……）里都加上一条 `Settings_Language_xxx`。

原因：下拉菜单会列出所有被发现的语言，而每一项的显示名是用**当前激活的语言**
去解析的。如果 `Strings/en-US/Resources.resw` 里缺少 `Settings_Language_ja`，
那么当程序运行在英文模式下时，“日本語”这一下拉项就会显示成**空白**。

因此，每新增一种语言 `xxx`，都要在已有的各个语言文件中加上：

```xml
<data name="Settings_Language_xxx" xml:space="preserve">
    <value><用本语言书写的 xxx 的显示名></value>
</data>
```

如果当前语言里缺少某个 `Settings_Language_<code>` key，程序会回退到该文化的
本地名（通过 `CultureInfo` 获取），不会崩溃——但加上 key 能保证文案统一、可控。

## 补充说明

- 只有包含 `Resources.resw` 的子文件夹才会被注册，空文件夹会被忽略。
- `DefaultLanguage` 优先取 `zh-CN`，若不存在则取第一个被发现的语言。
- 语言代码必须是 `.NET` 能识别的有效文化名（回退显示名时会用到 `CultureInfo`）。

[English]: ./README.md