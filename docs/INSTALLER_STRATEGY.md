# Installer Strategy

## Decision

`1.0.0-beta.6` 继续采用 **Inno Setup、per-user、x64、self-contained Desktop 与 Tools**。Bridge 与 Add-In 保持 `net472/x64`，依赖系统 .NET Framework 4.7.2+ 和本机 EPLAN 2.9 Runtime。

## Why Inno Setup

- 项目是传统 Win32/WPF 加 `net472` Bridge/Add-In 的多目录产品，不需要 MSI 企业编排。
- 官方 Inno Setup 支持 `PrivilegesRequired=lowest`，可在不请求 UAC 的情况下写入用户 LocalAppData Programs。
- `ArchitecturesAllowed=x64compatible` 可明确拒绝不适合的架构。
- 自带标准升级/卸载机制，脚本可在安装后生成含真实绝对 Desktop 路径的 Add-In 本地配置。
- 安装器不尝试静默注册 EPLAN Add-In；官方 EPLAN Add-Ins 对话框仍是可信交互边界。

官方依据：

- https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm
- https://jrsoftware.org/ishelp/topic_setup_architecturesallowed.htm
- https://jrsoftware.org/ishelp/topic_setup_uninstallable.htm

## Alternatives considered

- **WiX Toolset**：支持 MSI 与 per-user scope，适合集中部署和复杂组件规则，但当前个人工具的构建/维护成本更高。参考：https://docs.firegiant.com/wix/schema/wxs/packagescopetype/
- **MSIX**：包隔离、只读安装目录和外部宿主加载扩展的约束会增加 EPLAN 进程加载 Add-In 与跨进程共享 LocalAppData/IPC 的验证成本。Microsoft 的桌面打包准备文档明确要求审查由包外进程加载的 in-process extensions。参考：https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-prepare

## Runtime choice

- Desktop、EplanEdzIndex、EplanEdzProbe：`win-x64 --self-contained true`，目标机不需要 .NET SDK 或单独安装 .NET 8 Runtime；代价是包体积更大。
- Bridge / Add-In：`net472/x64`，不把 EPLAN DLL 复制到输出或安装包。
- Installer 会检测系统是否至少具备 .NET Framework 4.7.2；缺失时给出前置条件提示，但不会以提升应用运行权限来掩盖问题。
- 无 EPLAN：安装和 Desktop 启动不阻塞，进入 Offline Mode。
- Release 关闭并排除 PDB，避免把开发机源码/PDB 路径带入 Beta 发布物。

## Upgrade and uninstall

- 固定 Inno `AppId`，升级替换应用文件但不删除 LocalAppData 用户数据。
- 卸载器始终保留 LocalAppData 用户数据；清理必须在导出备份后由用户手工执行。
- installer 只递归删除 `{app}`。外部 EDZ Library 不在 `{app}` 或 LocalAppData 管理范围内，永远不删除。

## Portable ZIP

生成同一五层发布布局的 x64 ZIP。它默认仍使用 LocalAppData；本 Beta 不实现 portable data flag，以避免安装版/portable 两套隐含状态。

Portable Add-In 示例配置使用相对路径 `..\Desktop\EplanEdzManager.Desktop.exe`；安装版配置由 Installer 按实际安装根目录生成。
