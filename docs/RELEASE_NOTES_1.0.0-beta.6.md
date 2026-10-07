# EPLAN EDZ Manager 1.0.0-beta.6

发布日期：2026-10-07
状态：未签名 Beta，Windows x64

## 本次更新

- 再次完成中文界面校正，统一主窗口、目录、详情区和资源相关文本。
- 修复部分 EPLAN 多语言描述中出现的 `??`、语言标记残留和可恢复文本显示异常。
- 修复大型但有效的 EDZ 目录可能被小型条目保护规则误判的问题。
- 新增双击部件打开图片预览：显示 EDZ 中首个受支持的内嵌图片，可能是 2D 产品图，也可能是厂商预先提供的 3D 渲染图。
- “导入所选部件至 EPLAN 数据库”八步向导完成中文化，包括路径选择、状态、差异、操作、进度、错误和结果提示。
- 桌面快捷方式与 EPLAN Add-In 入口统一指向同一安装版本，并显式使用应用图标。

## 安全边界

- 图片预览只读取 EDZ 内已有资源；本版本不提供实时 EMA 或 3D 模型渲染器。
- 源 EDZ 继续只读打开，不会被覆盖。
- MDB 导入仍要求明确选择已关闭的目标 MDB，并经过检查、预览、备份、最终确认和导入后验证。
- 已存在或冲突部件默认跳过；本版本仍不提供 Selective Update。
- 发布包不包含 `Eplan.EplApi.*.dll`、EDZ、MDB、项目文件或用户索引数据。

## 软件展示

- [中文主目录与部件详情](images/main-window.png)
- [双击部件图片预览](images/double-click-part-preview.png)
- [中文安全导入向导](images/safe-import-zh.png)
- [我的部件库来源缺失状态](images/my-library-source-status.png)

## Verification

- Release build：0 warnings / 0 errors。
- 自动化测试：171/171 通过，其中包含 14 个本机 EPLAN 2.9 Bridge 集成测试。
- 安装器、Portable ZIP、离线启动、UTF-8 Add-In 配置、桌面入口一致性和卸载烟雾测试通过。
- 已验证 EPLAN 版本：`2.9.4.14642`；其它 2.9.x 不标记为已验证，EPLAN 2022+ 不受此构建支持。

## 下载提醒

这是未签名 Beta。请保留 Windows Defender、SmartScreen 和其它安全保护，并使用 GitHub Release 同时提供的 `SHA256SUMS.txt` 核对下载文件。
