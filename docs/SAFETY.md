# Safety Boundaries

适用范围：EPLAN EDZ Manager Beta Desktop、只读 Reader 及官方 EPLAN 适配器。  
首要原则：**原始 EDZ 永远只读；禁止直接修改 EPLAN 内部数据库文件；所有可变操作先进入隔离 Temporary Workspace。**

## 1. 不可违反的边界

1. 不原地修改、重命名、解压覆盖或重新压缩用户原始 EDZ；
2. 不直接读写 EPLAN 内部数据库表或数据库文件结构；只使用官方 API/GUI；
3. 不把本机 EPLAN DLL、ERX、许可证文件或用户数据库提交到 Git；
4. 不把 API Key、Token、密码、许可证信息上传到网络/云端；密钥只能来自本地环境变量或本地受保护配置；
5. 不用 EPLAN 2022+ 的 API 契约替代 2.9 证据；
6. 不实现自制 EDZ Writer；选择性导出使用 EPLAN 官方 API 并验证输出；
7. MDB 导入必须经过 Inspect / Preview / Backup / 明确确认 / Verify；不自动进行项目部件赋值或宏放置。

## 2. 风险分级

| 严重度 | 风险 | 可能后果 | 控制措施 |
|---|---|---|---|
| Critical | 直接修改用户 Parts Database | 库损坏、字段/索引不兼容、不可恢复 | 永不直改；新建隔离临时库；官方 API；操作前备份与路径断言 |
| Critical | 导入/导出误指向生产数据库 | 覆盖已有 Part、重复记录、资源目录污染 | 保存当前 DB；只允许白名单临时路径；新空库；`finally` 恢复；执行前显示/记录绝对路径 |
| High | 自制 EDZ 缺依赖或格式不兼容 | 宏/图片/construction 丢失、EPLAN 导入失败 | 优先官方 Exporter；Writer 延后；manifest 闭包与 GUI round-trip 双重验证 |
| High | 恶意/损坏归档 | 路径穿越、磁盘耗尽、内存耗尽、解析器崩溃 | 只读流、entry/总量/压缩比上限、路径正规化、取消与超时、拒绝加密/未知容器 |
| High | 版本/架构不匹配 | Add-In 加载失败、进程崩溃 | 固定 2.9.4.14642、net472、x64；构建时版本检查；不混用新版 DLL |
| High | API 许可证/Runtime 不可用 | 功能在开发机可用、目标机失败 | 启动前 capability check；清晰降级为 Offline Reader；不绕过许可证 |
| Medium | 同名部件冲突 | 覆盖或混合用户现有 Part | 临时库先验证；生产导入由用户确认；默认不覆盖；显示冲突清单 |
| Medium | 共享资源遗漏 | 多部件导出后部分资源缺失 | 以 manifest 建去重依赖图；由官方 Exporter 最终打包；导出后验证所有 locator |
| Medium | 跨版本 EDZ | 新字段丢失、旧版不能导入 | 保存原始 XML/未知字段；标记来源版本；2.9 回读作为接受门槛 |
| Medium | 外部文档 URL | 隐私泄露、意外联网、恶意链接 | 默认只显示，不自动访问/下载；需要用户显式操作；URL scheme 白名单 |
| Low | Add-In UI/启动失败 | EPLAN 启动变慢或功能不可用 | Add-In 独立、可禁用；离线 Reader 不依赖 Add-In；日志与版本诊断 |

## 3. 原始 EDZ 只读策略

- 以 `FileAccess.Read`、`FileShare.Read` 打开；不请求写权限；
- 扫描开始记录绝对路径、大小、mtime、SHA-256；关键操作结束后可复核 SHA-256；
- 任何需要提取的内容都写入新建的随机临时目录；
- 输出 EDZ 使用不同目录和新文件名，默认 `CreateNew`，禁止静默覆盖；
- 取消/异常只清理自己的临时目录，不触碰源目录；
- 临时目录必须先解析成绝对路径，并验证位于应用专用 temp root 下。

## 4. 归档安全

在读取 entry 前必须：

- 拒绝绝对路径、盘符路径、UNC、设备路径、`..` 路径穿越；
- 对分隔符、Unicode 和 `.` 片段做正规化后再验证；
- 设置最大 entry 数、单 entry 未压缩大小、总未压缩大小、manifest 大小、XML 深度、属性/文本长度与压缩比；
- 禁用 XML DTD 和外部实体；
- 不执行宏、脚本、EXE，也不主动打开外部 URL；
- 未知 entry type 作为 opaque metadata 保留；预览时仅允许明确白名单的图像/PDF，并在隔离进程/控件中处理；
- 记录重复路径、大小写冲突和 manifest 缺失/悬空引用。

具体阈值需用真实样本基准后设定；不得用欧姆龙 1.90 GB 未压缩总量作为恶意包的无限许可。

## 5. Temporary Workspace

建议布局：

```text
temp/<operation-id>/
  input-metadata.json
  logs/
  extracted/          # 仅按需资源
  parts-db/           # 官方 API 烟雾测试专用
  output-staging/
```

创建后记录 operation id、源哈希和 EPLAN/API 版本。仅删除该明确、已验证位于 temp root 内的 operation 目录。异常后可保留诊断日志，但日志不能包含许可证、凭据或不必要的用户隐私。

## 6. EPLAN Parts Database 安全

本地官方导出烟雾测试必须满足：

1. 用户正常 EPLAN 部件数据库已备份，且测试不指向它；
2. 用 `MDPartsManagement.CreateDatabase` 在专用 temp 下创建新空库；
3. 读取并保存 `PartsService.PartsDatabase` 的原值；
4. 切换前验证目标绝对路径位于当前 operation temp；
5. 导入模式优先 `AppendNewRecords` 到空库；
6. 所有 API 调用在单一受控 EPLAN 会话中执行，避免用户同时操作部件管理；
7. 用 `MDPartsDatabase` 只读核对计数/Part Number；
8. `finally` 恢复原数据库；若恢复失败，立即停止并提示人工处理；
9. 不通过 SQL 客户端、SQLite、Access 驱动或文件补丁直接编辑库；
10. 不把临时库默认为用户生产库。

## 7. 官方导出输出验证

输出成为“可交付 EDZ”前至少通过：

- 输出文件为新文件，哈希与长度记录完整；
- 归档测试通过；
- manifest 可解析，所有 locator 存在，无路径穿越；
- 部件集合等于所选集合加上经证明必需的依赖，不多不少；
- 图片、EMA、construction 等资源闭包完整；
- 在一次性临时部件数据库中由 EPLAN 2.9 官方接口/GUI成功回读；
- 回读后 Part Number、字段与资源链接一致；
- 不覆盖用户已有部件。

只通过 7-Zip 完整性测试不代表 EPLAN 语义有效。

## 8. EPLAN DLL、ERX 与 Git

- `.gitignore` 排除 `eplan-api/`、`*.edz`、`temp/`、`output/`、`Directory.Build.props.local`；
- `EPLAN29_API_DIR` 只指向本机合法安装/用户已提供副本；
- 不把 `.erx` 当成普通引用、插件依赖或可再分发文件；
- 构建产物不自动收集整个 EPLAN Bin；
- README 只记录获取方式与版本要求，不附带专有二进制。

## 9. Add-In 与进程边界

- 离线 Reader 即使 EPLAN 未安装也应工作；
- `EplanApi`/`EplanAddIn` 单独程序集，故障不得拖垮离线扫描；
- 加载前检查 x64、net472、API file version、EPLAN 进程状态与许可；
- Add-In 未来应支持完全禁用/卸载，不在启动时执行数据库迁移；
- 不从 WPF UI 线程直接做大归档扫描或长时间 EPLAN 调用；需取消、进度和严格串行化。

## 10. API 密钥与隐私

当前项目不需要云端 API Key。若未来加入外部服务：

- 禁止在对话、源码、日志或配置示例中写明文 Key；
- 只从环境变量或本地受保护配置读取；
- `.env` 必须被 Git 忽略；
- 任何 EDZ、部件数据、宏、图片和文档默认不上传网络；
- 网络功能必须独立、可关闭，并先获得用户明确授权。

