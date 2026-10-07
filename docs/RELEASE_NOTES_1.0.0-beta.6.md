# EPLAN EDZ Manager 1.0.0-beta.6

发布日期：2026-10-07
状态：未签名 Beta，Windows x64

这版主要处理界面中文、描述乱码和部件预览。安装包与 Portable ZIP 都已重新构建。

## 改了什么

- 主窗口、目录、详情区和资源页的中文重新检查了一遍。
- EPLAN 多语言字段里的 `??` 和语言标记现在会在显示前清理，能恢复的中文会正常显示。
- “导入所选部件至 EPLAN 数据库”八步窗口已汉化，状态、差异、进度和错误提示也改成了中文。
- 放宽了 EDZ 大型资源的单项限制，避免把正常的大型厂商库当成异常压缩包；总大小限制仍然保留。
- 双击部件行可以看 EDZ 里的图片。程序取第一张支持的图片，可能是 2D 产品图，也可能是厂家提供的 3D 效果图。
- 桌面快捷方式和 EPLAN Add-In 使用同一套 beta.6 文件，快捷方式图标也已补上。

## 目前仍有的限制

- 图片窗口只显示 EDZ 里已经存在的图片，不会实时渲染 EMA 或 3D 模型。
- 只有 EPLAN `2.9.4.14642` 做过完整验证。
- MDB 导入只支持已关闭、可独占访问的 EPLAN 2.9 Access MDB；已有或冲突部件默认跳过。
- 安装包未签名，SmartScreen 可能提示“未知发布者”。

## 软件展示

- [中文主目录与部件详情](images/main-window.png)
- [双击部件图片预览](images/double-click-part-preview.png)
- [中文安全导入向导](images/safe-import-zh.png)
- [我的部件库来源缺失状态](images/my-library-source-status.png)

## 测试情况

- Release 构建：0 warnings / 0 errors。
- 自动化测试：171/171，其中 14 项使用本机 EPLAN 2.9 Bridge。
- 安装、卸载、Portable ZIP、离线启动、Add-In 配置和桌面入口都跑过冒烟测试。
- 发布包检查过，不含 `Eplan.EplApi.*.dll`、EDZ、MDB、项目文件或用户索引数据。

## 下载

安装版、Portable ZIP 和 `SHA256SUMS.txt` 都在 [v1.0.0-beta.6 Release](https://github.com/shitou2226/EplanEdzManager/releases/tag/v1.0.0-beta.6)。建议保留 Windows Defender 和 SmartScreen，并在安装前核对 SHA-256。
