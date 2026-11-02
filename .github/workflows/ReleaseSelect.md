## 下载选择

带 `runtime` 的压缩包已内置 .NET 运行时与 Windows App SDK 运行时, 解压后可直接运行; 不带 `runtime` 的压缩包体积更小, 但需要系统已安装对应运行时.

### GitHub 下载

| 文件名 | 架构 | 开箱即用 | 描述 |
| --- | --- | --- | --- |
| [MemeManager-win-x64-runtime-{{TAG}}.zip](https://github.com/shuiping233/MemeManager/releases/download/{{TAG}}/MemeManager-win-x64-runtime-{{TAG}}.zip) | x64 | 是 | 内置 .NET 与 Windows App SDK 运行时, x64 用户首选 |
| [MemeManager-win-arm64-runtime-{{TAG}}.zip](https://github.com/shuiping233/MemeManager/releases/download/{{TAG}}/MemeManager-win-arm64-runtime-{{TAG}}.zip) | arm64 | 是 | 内置 .NET 与 Windows App SDK 运行时, 适用于 Windows on ARM 设备 |
| [MemeManager-win-x64-{{TAG}}.zip](https://github.com/shuiping233/MemeManager/releases/download/{{TAG}}/MemeManager-win-x64-{{TAG}}.zip) | x64 | 否 | 仅含应用本体, 需自行安装 .NET 10 桌面运行时与 Windows App SDK 运行时 |
| [MemeManager-win-arm64-{{TAG}}.zip](https://github.com/shuiping233/MemeManager/releases/download/{{TAG}}/MemeManager-win-arm64-{{TAG}}.zip) | arm64 | 否 | 仅含应用本体, 需自行安装 .NET 10 桌面运行时与 Windows App SDK 运行时 |
| [MemeManager_{{APPX_VERSION}}_x64.msix](https://github.com/shuiping233/MemeManager/releases/download/{{TAG}}/MemeManager_{{APPX_VERSION}}_x64.msix) | x64 | 否 | 旁加载安装包(已自签名), 需先把 MemeManager-sign.cer 导入「受信任的根证书颁发机构」 |
| [MemeManager_{{APPX_VERSION}}_arm64.msix](https://github.com/shuiping233/MemeManager/releases/download/{{TAG}}/MemeManager_{{APPX_VERSION}}_arm64.msix) | arm64 | 否 | 旁加载安装包(已自签名), 需先把 MemeManager-sign.cer 导入「受信任的根证书颁发机构」 |
| [MemeManager-sign.cer](https://github.com/shuiping233/MemeManager/releases/download/{{TAG}}/MemeManager-sign.cer) | 全架构 | — | 自签名根证书, 安装 MSIX 前导入「受信任的根证书颁发机构」 |

### CNB 下载

| 文件名 | 架构 | 开箱即用 | 描述 |
| --- | --- | --- | --- |
| [MemeManager-win-x64-runtime-{{TAG}}.zip](https://cnb.cool/shuiping233/MemeManager/-/releases/download/{{TAG}}/MemeManager-win-x64-runtime-{{TAG}}.zip) | x64 | 是 | 内置 .NET 与 Windows App SDK 运行时, x64 用户首选 |
| [MemeManager-win-arm64-runtime-{{TAG}}.zip](https://cnb.cool/shuiping233/MemeManager/-/releases/download/{{TAG}}/MemeManager-win-arm64-runtime-{{TAG}}.zip) | arm64 | 是 | 内置 .NET 与 Windows App SDK 运行时, 适用于 Windows on ARM 设备 |
| [MemeManager-win-x64-{{TAG}}.zip](https://cnb.cool/shuiping233/MemeManager/-/releases/download/{{TAG}}/MemeManager-win-x64-{{TAG}}.zip) | x64 | 否 | 仅含应用本体, 需自行安装 .NET 10 桌面运行时与 Windows App SDK 运行时 |
| [MemeManager-win-arm64-{{TAG}}.zip](https://cnb.cool/shuiping233/MemeManager/-/releases/download/{{TAG}}/MemeManager-win-arm64-{{TAG}}.zip) | arm64 | 否 | 仅含应用本体, 需自行安装 .NET 10 桌面运行时与 Windows App SDK 运行时 |
| [MemeManager_{{APPX_VERSION}}_x64.msix](https://cnb.cool/shuiping233/MemeManager/-/releases/download/{{TAG}}/MemeManager_{{APPX_VERSION}}_x64.msix) | x64 | 否 | 旁加载安装包(已自签名), 需先把 MemeManager-sign.cer 导入「受信任的根证书颁发机构」 |
| [MemeManager_{{APPX_VERSION}}_arm64.msix](https://cnb.cool/shuiping233/MemeManager/-/releases/download/{{TAG}}/MemeManager_{{APPX_VERSION}}_arm64.msix) | arm64 | 否 | 旁加载安装包(已自签名), 需先把 MemeManager-sign.cer 导入「受信任的根证书颁发机构」 |
| [MemeManager-sign.cer](https://cnb.cool/shuiping233/MemeManager/-/releases/download/{{TAG}}/MemeManager-sign.cer) | 全架构 | — | 自签名根证书, 安装 MSIX 前导入「受信任的根证书颁发机构」 |

`*_unsigned.msix` 仅供 Microsoft Store 提交, 未签名, 普通用户请勿直接安装.
