# Developer Guide

## Architecture

- `Core` (`netstandard2.0`)：EDZ domain model、locator safety、stable identity；无 SQLite/EPLAN 依赖。
- `Edz` (`netstandard2.0`)：SharpCompress read-only EDZ parser。
- `Infrastructure.Sqlite` (`net8.0`)：schema migrations、Catalog、My Library、maintenance。
- `Application` (`net8.0-windows`)：用例、搜索、backup、diagnostics、EPLAN detection、Bridge orchestration。
- `Desktop` (`net8.0-windows`, WPF x64)：UI、single instance、First Run、productization UX。
- `EplanBridge.Protocol` / `AddIn.Protocol` (`netstandard2.0`)：本地 IPC contracts。
- `EplanBridge` / `EplanApi` / `EplanAddIn` (`net472/x64`)：唯一可引用 EPLAN API 的边界。

Desktop 不直接引用 Infrastructure；主依赖保持 `Desktop -> Application -> Infrastructure`。EPLAN API 不得进入 Core、Edz、Infrastructure、Application 或 Desktop。

## Versioning

`Version.props` 是唯一产品版本源。当前 SemVer 为 `1.0.0-beta.6`，Assembly/File Version 为 `1.0.0.0`。发布脚本把当前 Git commit 作为 `SourceRevisionId` 注入 InformationalVersion；Installer、报告和 About 使用同一版本。

## Local EPLAN paths

开发构建通过环境变量或 git-ignored `Directory.Build.props.local` 提供 `Eplan29ApiDir`、`Eplan29PlatformBinDir` 和 `Eplan29VariantBinDir`。不要提交本机路径或 EPLAN 二进制。

运行时优先使用 First Run 保存的 Platform/Variant Bin，其次环境变量与注册信息，并读取 `EPLAN.exe` / API DLL metadata。只验证 `2.9.4.14642`；其它 2.9.x 是 unverified。

## Build and tests

```powershell
dotnet restore EplanEdzManager.sln
dotnet build EplanEdzManager.sln -c Release --no-restore
dotnet test EplanEdzManager.sln -c Release --no-build
```

`build-release.ps1` 默认运行 fast/regular tests、self-contained Desktop publish、Bridge/Add-In build、五层 artifact collection、EPLAN DLL exclusion、portable ZIP、Inno installer、可选 installer smoke 和 SHA-256。`-IncludeEplanIntegration` 才运行耗时的真实 EPLAN Bridge integration tests。

## Release layout

```text
EplanEdzManager/
  Desktop/
  Bridge/
  AddIn/
  Tools/
  Docs/
```

发布门禁拒绝 EPLAN API DLL、Tests、Samples、bin、obj、EDZ 和数据库文件。

## Database/settings migrations

SQLite migration 使用 embedded `Migrations/NNN_name.sql` 与 `schema_migrations`。迁移必须向前兼容且保留 My Library。Settings 使用 JSON 兼容反序列化和 `SettingsSchemaVersion` 默认值；新增字段必须有安全默认值。

## Safety constraints

- EDZ source read-only；测试前后校验真实样本 SHA。
- 不写 EPLAN Project Data，不自动 Part Assignment，不 Macro Placement。
- 不在线修改活动 Parts Database。
- Safe import 保持 Preview / explicit confirmation / Backup / Verification / Audit。
- Orphan Cleaner 只能删除精确 Temp Root 直属、GUID+marker 匹配、超过阈值、无活动 Bridge PID、无 reparse 的 session；禁止对根目录递归删除。
- 诊断包不包含 DB、EDZ、MDB、凭据或项目内容。
- API Key/Token 只能从环境变量或本机配置读取，禁止上传。

## Phase history

Phase 0–2 建立研究、EDZ reader、官方 exporter、SQLite 与 WPF；Phase 3 建立 durable My Library；Phase 4–6 建立 Bridge、安全 MDB Import 与 EPLAN Add-In；Phase 7 只做产品化和发布加固，不扩大业务架构。
