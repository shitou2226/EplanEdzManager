# Screenshot Plan

本次没有伪造或用设计稿冒充实际 UI 截图。由人工在最终 installer build 上按下列位置捕获，图片保存到 `docs/images/` 的预留位置，并在捕获后重新核对版本、数据脱敏和 DPI：

| Screenshot | 操作位置 | 必须显示 | DPI |
|---|---|---|---|
| Main Catalog | 启动后 Catalog | 搜索、EDZ Library、结果表、1.0 Beta | 100% / 150% |
| My Library | My Library | Favorite、Collection、Tag、Preferred Source | 100% / 150% |
| Export Dialog | 选择 Saved Parts → Export Selected EDZ | 选择数、来源、目标、确认边界 | 100% |
| Safe Import Wizard | Import Selected to EPLAN Parts DB | Inspect / Preview / Backup / Conflict / Confirmation | 100% |
| EPLAN Context Bar | Add-In Connected 后主窗口顶部 | EPLAN version、project/selection availability；不显示敏感项目路径 | 100% |
| Diagnostics | Environment Diagnostics | Green/Yellow/Red 加文字、EPLAN compatibility、SQLite、Bridge/Add-In | 100% / 200% |

截图前必须使用非敏感测试库；不得显示用户名、真实项目路径、客户名或完整诊断日志。
