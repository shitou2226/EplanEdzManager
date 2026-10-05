# User Guide — EPLAN EDZ Manager 1.0 Beta

## 1. First Run Setup

首次启动依次完成 Welcome、EPLAN 检测、API 检查、EDZ Library Folder、SQLite 数据库位置、Add-In 状态和完成页。检测只读取注册信息、已保存配置与 `EPLAN.exe` / API DLL 文件元数据。没有 EPLAN 时选择 Offline Mode 即可。

EPLAN 兼容性显示规则：

- `2.9.4.14642`：Compatible / Verified。
- 其它 `2.9.x`：Detected but not verified；Catalog/My Library 可离线使用，但 Bridge Export/Import 被禁用。
- `2022+` 或非 2.9：Unsupported by this build。

## 2. Catalog and Search

在“EDZ 库”选择“添加目录”，登记源目录后点击“重新扫描”或“扫描全部库”。应用只读打开 `.edz`；不会重写、移动或删除源文件。搜索支持全部字段、Part Number、Type Number、Manufacturer、Description，以及全文、包含和精确匹配。

## 3. My Library

从 Catalog 把部件加入 My Library 后可维护 Favorite、Collection、Tag、Note。来自多个 EDZ 的同一逻辑部件会共享稳定身份，但保留多个 Source Candidate；Preferred Source 必须由用户显式选择，系统不会静默改选。

Backup My Library 生成 metadata-only JSON。Restore 始终先显示 Preview，并以事务合并；不复制 EDZ、图片、宏或 EPLAN DLL。

## 4. Resources

部件详情按图片、宏、文档和其它资源显示。预览与导出按需读取所选 EDZ entry。Source Missing 或 Missing resource 会在 Library Health 和详情中显示，不会伪造文件存在。

## 5. Export Selected EDZ

选择 Catalog 或 My Library 部件，确认 Preferred Source 存在，然后执行 Export Selected EDZ。Desktop 会启动隔离 Bridge，检查 EPLAN Runtime、PartsService、EDZ Converter 和临时数据库能力，再调用官方 EPLAN 2.9 导出能力。失败不会回退到自定义 EDZ Writer。

## 6. Safe MDB Import

导入向导执行 Inspect → Preview → Conflict → 用户显式确认 → Backup → 官方 Import → Verification → Audit。目标是用户明确选择的 MDB；不会自动修改活动 Parts Database，也不会写 Project Data。

Beta 仅支持关闭且可独占访问的 EPLAN 2.9 Access MDB。SQL Server、活动/锁定 MDB、自动 schema 升级和 Selective Update 不支持。导入使用 `AppendNewRecords`：已有或冲突部件默认 Skip；同一个已验证 EDZ 中的 New 行只能全部导入或全部跳过。

## 7. EPLAN Add-In

进入 Environment Diagnostics → EPLAN Add-In Setup：

1. 打开 EPLAN。
2. 打开 Options / API Add-Ins。
3. 选择界面显示的 `EplanEdzManager.EplanAddIn.dll` 实际路径。

状态含义：Not detected、Connected、Recently connected、Disconnected。DLL 文件存在不等于 Installed。

Selected Parts Context 仅在 EPLAN 通过已覆盖的只读 API 暴露部件分配时可用；否则显示 `Unavailable`。程序不会从活动部件库猜测，也不会因此写 Project Data。

## 8. Environment Diagnostics

能力列表用 Green / Yellow / Red 并附文字状态，覆盖 Desktop、SQLite、EDZ Reader、SharpCompress、EPLAN、API、Bridge、Add-In、PartsService、Converter 和 MDB Import。

“Run Diagnostics”在本地生成 `DiagnosticReport-*.json`；“Export Diagnostic Bundle”生成脱敏 ZIP。报告包含版本、OS、.NET、EPLAN、配置路径、schema、EDZ folders、Bridge/Add-In 状态和近期日志摘要，但不包含 EDZ、MDB、`index.db`、凭据或项目内容。

## 9. Maintenance

- VACUUM / Optimize：只在用户点击并确认时执行，不在启动时自动执行。
- Check Library Health：检查目录/来源缺失、stale index、Broken EDZ、Missing resource、Orphan Saved Source，并执行 SQLite integrity/foreign-key 检查。
- Rebuild Catalog Index：清空并重新扫描 Catalog；My Library、Favorite、Collection、Tag、Note 和 Backup 保留并重新绑定。
- Reset Application Settings：保留数据库/My Library，下次启动重新显示 First Run Setup。
- Temporary Sessions：必须先 Dry Run；Clean 只处理通过全部安全门槛的旧孤儿 Bridge session。

## 10. Logs and crashes

日志目录为 `%LOCALAPPDATA%\EplanEdzManager\Logs\Desktop|Bridge|AddIn`，默认保留 14 天。严重 Desktop 异常写入 `%LOCALAPPDATA%\EplanEdzManager\CrashReports\CrashReport-*.json`，提示位置后安全退出。Bridge/Add-In 继续 fail soft，不让诊断异常破坏 EPLAN。
