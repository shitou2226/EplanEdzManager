# EPLAN 2.9 Add-In Installation

## Prerequisites

- Windows x64。
- 已验证版本：EPLAN Electric P8 `2.9.4.14642` x64。其它 2.9.x 只检测、不启用 Add-In 功能；2022+ 不受支持。
- .NET Framework 4.7.2 或更高版本。Desktop 自带 .NET 8 运行时，不要求 Visual Studio、.NET SDK、MSBuild、NuGet 或 Git。
- EPLAN API DLL 只从用户本机已安装的 EPLAN Runtime 加载；安装包不包含也不下载 `Eplan.EplApi.*.dll`。

## Setup 安装版

Setup 以当前普通用户安装到 `%LOCALAPPDATA%\Programs\EplanEdzManager`，并在 `AddIn` 目录生成 UTF-8 配置。安装器不会复制文件到 EPLAN 的 Program Files，也不会修改未验证的注册表项。

在 Desktop 的 `Environment Diagnostics → EPLAN Add-In Setup` 中复制实际 DLL 路径，然后在 EPLAN 中：

1. 打开 `Utilities / 工具 → API Add-Ins / API 模块`。
2. 选择官方对话框中的加载/注册命令。
3. 选择界面显示的 `EplanEdzManager.EplanAddIn.dll`。
4. 确认 Add-In 已列出并重启 EPLAN 一次。
5. 确认 `Tools / 工具 → EPLAN EDZ Manager` 菜单出现。

## Portable ZIP

Portable ZIP 不运行安装器。使用 Add-In 前：

1. 把 `AddIn\EplanEdzManager.AddIn.example.json` 复制为同目录的 `EplanEdzManager.AddIn.json`。
2. 保持默认相对配置：

```json
{
  "desktopExecutablePath": "..\\Desktop\\EplanEdzManager.Desktop.exe",
  "connectTimeoutMilliseconds": 5000
}
```

3. 在 EPLAN 官方 Add-In 对话框中选择 Portable 目录下的 Add-In DLL。

相对路径只允许解析到同一发布根目录的 sibling `Desktop\EplanEdzManager.Desktop.exe`；程序不会搜索磁盘或启动其它 EXE。

## Verify

无项目时点击 EPLAN 菜单，Desktop 应打开或激活一次，并显示已连接的 EPLAN 版本/PID；项目、页面或所选部件上下文无法可靠读取时必须显示 `Unavailable`，不得猜测。

只有 EPLAN 和 API 文件元数据都精确匹配 `2.9.4.14642` 时相关功能才可用。版本缺失或不匹配时，Desktop 继续 Offline Mode，Add-In/Bridge 安全禁用。

日志位于：

```text
%LOCALAPPDATA%\EplanEdzManager\Logs\AddIn\
%LOCALAPPDATA%\EplanEdzManager\Logs\Desktop\
```

## Unregister / Uninstall

先在 EPLAN 官方 API Add-Ins 对话框中移除准确的 Add-In DLL，再关闭 EPLAN。随后可运行应用卸载器。卸载器只删除 per-user 程序文件；保留 `%LOCALAPPDATA%\EplanEdzManager` 下的 index、My Library、settings、logs、diagnostics 和 backups，也不会删除外部 EDZ 源目录。

## Troubleshooting

- **菜单缺失：**确认选择的是当前安装目录中的 DLL，并重启 EPLAN；DLL 存在不等于已注册。
- **Desktop 未打开：**确认 `EplanEdzManager.AddIn.json` 与 DLL 同目录，且相对 Desktop 文件存在。
- **版本不可用：**打开 Environment Diagnostics；其它 2.9.x 不应被当成已验证版本。
- **协议不匹配：**Desktop、Add-In 与共享 Protocol DLL 必须来自同一个 Setup/ZIP。
- **Unsigned Beta：**当前版本未签名，Windows SmartScreen 可能提示未知发布者。不要关闭 Defender、不要关闭杀毒软件，也不要创建安全例外。
