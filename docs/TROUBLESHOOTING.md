# Troubleshooting

## EPLAN not detected

打开 Environment Diagnostics，核对 Platform Bin 是否含 `EPLAN.exe`，Variant Bin 是否为 Electric P8 2.9 对应 `Bin`，API Version 是否可读。可重新运行 First Run Setup。没有 EPLAN 时继续使用 Offline Mode。

## Invalid EPLAN path

状态显示 Invalid configuration 时修正或清空路径。Desktop 不会循环启动 Bridge；EPLAN 按钮保持 Unavailable，Catalog/Search/My Library 仍可用。

## Bridge timeout

运行 Diagnostics，检查 Bridge executable、EPLAN 版本、Platform/Variant Bin、.NET Framework 4.7.2+ 和最新 Bridge 日志。不要把其它 2.9.x 自动视为 Fully supported。

## MDB locked

关闭可能占用目标 MDB 的 EPLAN Parts Management 或其它程序，再重新 Inspect/Preview。应用不会绕过锁或直接写活动数据库。

## Add-In not shown

确认使用 EPLAN Options / API Add-Ins 交互选择界面显示的实际 DLL；核对同目录 `EplanEdzManager.AddIn.json` 与 Desktop 路径。DLL 存在不等于已注册，Desktop 只有收到 IPC Hello 才显示 Connected。

## EDZ damaged

Library Health 或“诊断”页会显示 Broken EDZ / parser error。保留原文件，向供应方重新获取；应用不会尝试重写损坏 EDZ。

## Source Missing / Missing resource

恢复原路径、重新登记/扫描目录，或在 My Library 选择其它可用 Preferred Source。Source Missing 不会删除 Saved Part。

## SQLite issue

检查数据库路径、LocalAppData 可写性和磁盘空间。先导出 My Library backup，再在 Environment Diagnostics 执行 Optimize/VACUUM。不要手工删除数据库来“修复”个人元数据。

## Orphan temp session

进入 Temporary Sessions，先 Dry Run。只有超过 24 小时且 marker、PID、GUID、规范化路径、reparse 检查全部通过的项才可 Clean。没有 marker 或随机目录会保留。

## Reset settings

Reset Application Settings 仅重置 settings 并让下次启动重新打开 First Run Setup；不会删除 `index.db` 或 My Library。

## Logs and diagnostic bundle

日志位于 `%LOCALAPPDATA%\EplanEdzManager\Logs`。通过 Help / Environment Diagnostics 导出脱敏 Diagnostic Bundle，提交问题时先审阅 ZIP 内容；它不含数据库或源 EDZ。
