# Privacy and Data

EPLAN EDZ Manager `1.0.0-beta.3` 默认是完全本地工具，不需要云服务，不上传数据，不包含 telemetry，也不会自动联系任何服务器。

## 本地保存的数据

- `%LOCALAPPDATA%\EplanEdzManager\settings.json`：数据库、导出、备份、EPLAN Platform / Variant Bin 等本机路径和界面设置。
- `%LOCALAPPDATA%\EplanEdzManager\index.db`（默认位置）：EDZ Catalog 索引与 My Library 元数据，包括 Favorite、Collection、Tag、Note 和来源绑定。用户可配置其它数据库位置。
- `%LOCALAPPDATA%\EplanEdzManager\Logs\Desktop|Bridge|AddIn`：统一结构化日志。
- `%LOCALAPPDATA%\EplanEdzManager\CrashReports`：异常类型、消息、Stack、最后操作、SessionId 和版本。
- `%LOCALAPPDATA%\EplanEdzManager\Diagnostics`：用户显式生成的诊断报告。
- `%LOCALAPPDATA%\EplanEdzManager\Backups`（默认位置）：My Library metadata-only JSON backup。用户可以明确选择其它可写目录。
- `%TEMP%\EplanEdzManager\Bridge\<session-guid>`：Bridge 的隔离临时会话，带本程序 marker；正常结束时清理。

## 路径与日志

日志可能记录：EDZ Library 路径、数据库路径、导出目标、EPLAN Platform / Variant Bin、操作结果、耗时、错误类型、版本、SessionId、Component、Timestamp、Severity 和 EventId。这样做是为了本机诊断；路径不会自动上传。

日志不应写入完整 EDZ 内容、项目文件内容、MDB 内容、密码、Token 或 API Key。诊断包导出还会对常见 credential 字段和 `sk-...` 模式再次脱敏。

## 明确不会上传或打包的内容

- 完整 EDZ 文件或其二进制内容。
- 用户 Parts Database / MDB。
- `index.db` 全文，除非未来提供独立且由用户明确选择的功能；当前版本没有该选择。
- 完整 EPLAN Project 文件内容。
- 密码、Token、API Key。
- EPLAN API DLL 或 EPLAN 文档。

## 卸载

卸载器始终保留 `%LOCALAPPDATA%\EplanEdzManager`，避免误删 index、My Library、设置、日志或备份。如需清理，请先导出 My Library 备份，再由用户手工删除明确的数据目录。外部 EDZ Library 永远不属于应用用户数据目录，卸载器绝不删除它。
