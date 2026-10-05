# EPLAN EDZ Manager

[English](README.md)

面向 EPLAN P8 2.9 的本地 Windows x64 部件库管理工具。只读索引和搜索 EDZ，整理个人部件库，并通过本机 EPLAN 官方 API 执行选择性 EDZ 导出和受保护的 MDB 导入。

**未签名 Beta：1.0.0-beta.3。** 本项目是独立非官方工具，与 EPLAN GmbH & Co. KG 无隶属关系，也未获得其背书。EPLAN 商标归其相应权利人所有。

## 功能

- 只读 EDZ 解析、SQLite 增量索引和 FTS 搜索。
- My Parts Library：收藏、集合、标签、首选来源跟踪。
- 图片/资源预览及已有资源导出。
- 通过本机 EPLAN API 执行官方选择性 EDZ 导出。
- 安全 MDB 导入：检查、预览、明确确认、备份、验证。
- EPLAN 2.9 Add-In：向 Desktop 传递本地上下文。
- 无可用且已验证的 EPLAN 环境时进入离线模式。

## 截图

暂未提供真实软件截图。[截图位置与隐私要求](docs/images/README.md)预留在 `docs/images/`。不会用生成图片或设计稿冒充运行截图。

## 架构

```text
.NET 8 Desktop (WPF x64)
       | Application -> SQLite / 只读 EDZ Reader
       |
       +-- Named Pipe -- net472 x64 Bridge -- 本机 EPLAN 2.9 API
       |
       +-- Named Pipe -- net472 x64 Add-In (运行于 EPLAN 内)
```

EPLAN 引用隔离在集成边界；Desktop、Reader 和 SQLite 层不引用 EPLAN 专有 DLL。参见[开发说明](docs/DEVELOPER_GUIDE.md)和 [Add-In 架构](docs/EPLAN_ADDIN_ARCHITECTURE.md)。

## 环境要求

- Windows x64。
- Desktop 与发布版命令行工具使用自包含 .NET 8；用户无需 Visual Studio、.NET SDK、Git 或 NuGet。
- 离线索引、搜索、My Parts Library、资源预览/导出不需要 EPLAN。
- 连接功能需要有效的本机 EPLAN 安装及对应 API 能力/许可。唯一已验证版本为 **EPLAN P8 2.9.4.14642**。
- Bridge / Add-In 需要 .NET Framework 4.7.2 或更新版本。缺少或版本不兼容时安全禁用连接功能。

## 安装

使用 Beta Release 的 Setup 或 Portable ZIP；源码目录不是安装包。

### Setup

运行 `EplanEdzManager-1.0.0-beta.3-x64-Setup.exe`。安装器采用 per-user 安装，无需管理员权限。用户数据写入 `%LOCALAPPDATA%\EplanEdzManager`，不写安装目录或 EPLAN 目录。

### Portable

解压 `EplanEdzManager-1.0.0-beta.3-x64.zip`，启动 `EplanEdzManager\Desktop\EplanEdzManager.Desktop.exe`。必须保留完整目录结构。“Portable”指免安装分发；持久数据仍在 LocalAppData，临时会话使用用户 Temp。

使用 `SHA256SUMS.txt` 核验下载。Beta 尚未签名，Windows 可能显示信任提示。不要关闭 Defender、SmartScreen 或其它安全保护；遵守所在组织的软件审批要求。

参见[用户指南](docs/USER_GUIDE.md)、[故障排查](docs/TROUBLESHOOTING.md)、[干净机器验收清单](docs/CLEAN_MACHINE_TEST_CHECKLIST.md)。

## EPLAN Add-In

使用 EPLAN 的 Add-In 注册机制人工注册。安装器只安装 DLL 和配置，不修改 EPLAN 安装目录，也不自动注册。步骤见 [Add-In 安装说明](docs/EPLAN_ADDIN_INSTALLATION.md)。Portable 可使用 Add-In 旁的相对路径配置示例。

## 安全边界

- 源 EDZ 只读，不实现自定义 EDZ Writer。
- EDZ 导出使用 EPLAN 官方 API，验证通过后才生成最终输出。
- 不对 EPLAN MDB 执行直接 SQL 写入；本地 SQLite 索引是独立数据库。
- MDB 导入必须经过 Preview / Backup / 明确确认 / Verify，保留恢复备份。
- 不自动进行项目部件赋值或宏放置。
- 卸载保留个人数据，不删除源部件库。
- 不分发 EPLAN DLL、API 文档、ERX、厂商 EDZ 或真实 MDB。

参见[安全说明](docs/SAFETY.md)、[MDB 导入安全](docs/PARTS_DATABASE_IMPORT_SAFETY.md)、[隐私与数据](docs/PRIVACY_AND_DATA.md)。

## 已知限制

- Beta 软件；重要数据库应先复制备份，并独立复核结果。
- 仅验证 EPLAN 2.9.4.14642，不将其它版本视为已验证环境。
- 不提供 EMA/3D 渲染，可以导出已有资源。
- 已验证图片与 EMA 宏引用；PDF、结构、机械模型和附件资源的覆盖尚未建立。
- Add-In 上下文取决于 EPLAN API 可用信息；不支持的信息标记 Unavailable / Not Yet Supported，不推测。
- 暂无生产代码签名、自动更新器或公共 EPLAN 集成 CI。
- 不包含真实厂商回归样本，相关测试需自行提供合法本地输入。

## 构建

源码构建需要 Windows x64、支持 .NET 8 的 SDK 和 PowerShell 7。这是开发要求，不是用户运行要求。

### 无 EPLAN 构建

```powershell
pwsh -File scripts/Test-Offline.ps1
```

构建 Desktop 和工具，运行不依赖 EPLAN 的测试项目，排除真实样本集成用例。离线测试使用本地生成的人工数据，不下载或包含专有样本。

### 完整本地发布

通过本机环境变量 `EPLAN29_API_DIR` 或被忽略的 `Directory.Build.props.local` 指定自有授权安装中的 API DLL 目录，不要把 DLL 复制进仓库。开发构建还需 .NET Framework 4.7.2 targeting pack，安装器编译需 Inno Setup。

```powershell
pwsh -File build-release.ps1
```

连接测试还需本机 Platform/Variant Bin 配置与合法私有样本，详见[本地集成测试](docs/LOCAL_INTEGRATION_TESTS.md)。`-IncludeEplanIntegration` 明确启用真实 EPLAN 操作；`-IncludeLocalFixtures` 启用依赖真实样本的 Reader/Application 测试。

源码发布前对 staged 文件运行 `pwsh -File scripts/Test-PublicReleaseGate.ps1 -RequireLicense`。暂不展示 CI badge；公共 runner 无法在没有授权本机 EPLAN 的情况下执行连接集成测试。

## 许可证

项目自有源码采用所有者明确选择的 [MIT 许可证](LICENSE)。第三方组件遵守各自许可证；MIT 授权不涵盖 EPLAN 专有软件或用户数据。

## 第三方

[第三方声明](THIRD_PARTY_NOTICES.md)列出运行依赖与许可证。EPLAN 专有 DLL 是外部前置条件，不属于分发依赖。分享日志或诊断信息前，请阅读[贡献说明](CONTRIBUTING.md)和[安全说明](SECURITY.md)。
