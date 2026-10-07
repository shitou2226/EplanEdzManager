# Clean Machine Test Checklist — EPLAN EDZ Manager 1.0.0-beta.6

本清单用于 Windows Sandbox 或一次性 Windows x64 VM。验收机器不得包含源码、Visual Studio、VS Build Tools、.NET SDK、NuGet CLI、Git 或开发机 NuGet cache。使用最终 `Setup.exe` 或 Portable ZIP，不得从 `bin/Debug`、`bin/Release` 启动。

## 0. Evidence and safety

- [ ] 记录 Windows 版本、构建号、x64、测试用户名及是否为标准用户。
- [ ] 用 `SHA256SUMS.txt` 核对 Setup/ZIP；文件名必须为 `1.0.0-beta.6`。
- [ ] 保持 Microsoft Defender/SmartScreen 开启。当前为 Unsigned Beta；记录正常的未知发布者提示，不关闭 Defender、不添加安全例外。
- [ ] 确认测试账户不是 Administrators 成员；后续安装、启动和业务操作均不使用“以管理员身份运行”。

## 1. Per-user Setup

- [ ] 以标准用户运行 Setup；确认未出现 UAC 提权请求。
- [ ] 默认安装位置为 `%LOCALAPPDATA%\Programs\EplanEdzManager`，不写 Program Files、Windows、EPLAN 安装目录或磁盘根目录。
- [ ] 在自定义路径 `...\EPLAN Manager 中文 (Beta)-_` 再测一次安装；桌面快捷方式可正常启动。
- [ ] 如未安装 .NET Framework 4.7.2+，安装器给出明确提示；Offline Desktop 仍可安装。不得要求 .NET 8 Runtime，因为 Desktop 已自包含。
- [ ] 安装目录不含 `Eplan.EplApi.*.dll`、EPLAN XML API 文档、ERX、PDB、tests、samples、obj、开发脚本或数据库样本。

## 2. First startup and writable roots

- [ ] 首次启动不依赖 `dotnet.exe`、MSBuild、NuGet、Visual Studio 或 Git。
- [ ] Startup environment check 显示/验证 Install Root、x64 process、bundled .NET 8、LocalAppData、Temp 与 EPLAN capability；失败信息可读且不建议提权。
- [ ] 删除 `%LOCALAPPDATA%\EplanEdzManager` 后重新启动；按需创建 `index.db`、`settings.json`、`Logs`、`Backups`、`Diagnostics`、`CrashReports`。
- [ ] TEMP 下的 Bridge 根使用 `%TEMP%\EplanEdzManager\Bridge`，不存在时按需创建。
- [ ] 用 Process Monitor 复核应用不尝试写 Program Files、EPLAN 安装目录、Windows 或 `C:\` 根目录。

## 3. No-EPLAN / runtime-only mode

- [ ] 在未安装 EPLAN 的 VM 中启动；Desktop 进入 `Offline Mode`，不崩溃、不循环启动 Bridge。
- [ ] EPLAN / Bridge / Add-In 按钮明确显示 `Unavailable`；Catalog、Search、My Library、resource preview/export 仍可使用。
- [ ] 使用中文用户名、带空格用户名或等价测试 profile；SQLite/settings/log/temp 均落在该用户自己的 profile。
- [ ] 从 Portable ZIP 的 `Desktop` 启动，并用 `Tools\EplanEdzIndex` / `Tools\EplanEdzProbe` 验证 CLI；系统没有 .NET SDK/runtime 时仍可直接运行。

## 4. Offline functional path and path characters

- [ ] 建立 EDZ 目录 `EPLAN Parts Library 中文 (只读)-_`，路径同时覆盖空格、括号、中文、连字符和下划线。
- [ ] 把源 EDZ 设为只读；完成 Add Library、Scan、Search、Part Detail、My Library、图片预览和单资源导出。
- [ ] 核对扫描前后源 EDZ SHA-256、大小、修改时间不变；源目录无需写权限。
- [ ] 导出到中文+空格路径，确认 UTF-8 文案、Path API 和进程参数未拆分。
- [ ] 关闭再启动，确认 settings schema migration、索引和 My Library 仍可加载。

## 5. Verified EPLAN machine

- [ ] 安装 EPLAN Electric P8 x64 `2.9.4.14642`；Environment Diagnostics 必须通过 registry/install metadata 与实际 `EPLAN.exe` / API DLL file version 验证 Platform/Variant Bin。
- [ ] 临时配置不存在路径、其它 2.9.x 或 2022+；相关功能安全禁用，Desktop 继续 Offline Mode。
- [ ] Bridge 从本机 EPLAN Runtime 加载 API；发布目录仍不得出现 `Eplan.EplApi.*.dll`。
- [ ] Export Selected EDZ 完成 capability check、官方导出与结果验证；失败时不回退到自定义 EDZ writer。

## 6. MDB import

- [ ] 只选择关闭、可独占访问的 EPLAN 2.9 Access MDB。
- [ ] 依次完成 Inspect → Preview → explicit decisions → Backup → official Import → Verification → Audit。
- [ ] 锁定、只读、变更后的 MDB/EDZ 均被拒绝；AccessDenied 不通过管理员权限绕过。
- [ ] SQL Server、活动数据库、schema 自动升级、Selective Update 保持 Unsupported/Unavailable。

## 7. Add-In

- [ ] Setup 生成的 `AddIn\EplanEdzManager.AddIn.json` 使用实际 sibling Desktop 路径且为 UTF-8。
- [ ] Portable 示例使用 `..\Desktop\EplanEdzManager.Desktop.exe`；路径只能解析到同一发布根的 sibling Desktop。
- [ ] 通过 EPLAN 官方 API Add-Ins 对话框注册，不复制 DLL 到 EPLAN Program Files、不猜测 registry。
- [ ] 无项目与有项目各触发一次；IPC 可处理中文/空格安装路径，Selected Parts 无可靠上下文时显示 `Unavailable`。

## 8. Maintenance and cleanup

- [ ] Temporary Sessions 先 Dry Run；missing Temp/root 不报错。
- [ ] 对无权限或含 reparse point 的 session 执行扫描：必须 `Skip + Diagnostic`，不得崩溃或递归删除 Bridge root。
- [ ] My Library backup 默认位于 `%LOCALAPPDATA%\EplanEdzManager\Backups`；metadata-only restore 先 Preview。
- [ ] Diagnostic ZIP 不含 EDZ、MDB、index.db、密码、Token、API Key、项目内容或开发机绝对路径。

## 9. Uninstall

- [ ] 先在 EPLAN 官方对话框注销 Add-In，再以标准用户运行卸载器。
- [ ] 安装文件和生成的 Add-In config 被移除。
- [ ] `%LOCALAPPDATA%\EplanEdzManager`、外部 EDZ、My Library、backup、logs 与 diagnostics 默认保留。
- [ ] 卸载后无 Program Files/EPLAN 安装目录残留改动；不需要管理员权限完成清理。

## 10. Acceptance record

- [ ] Setup：PASS / FAIL
- [ ] Portable：PASS / FAIL
- [ ] No-EPLAN：PASS / FAIL
- [ ] Verified EPLAN：PASS / FAIL
- [ ] Standard-user permissions：PASS / FAIL
- [ ] Unicode/space/read-only paths：PASS / FAIL
- [ ] Add-In：PASS / FAIL
- [ ] Uninstall/data retention：PASS / FAIL

任一安全边界失败时，整体验收结论必须为 **FAIL**，不得用管理员权限、关闭杀毒软件或复制 EPLAN proprietary DLL 的方式规避。
