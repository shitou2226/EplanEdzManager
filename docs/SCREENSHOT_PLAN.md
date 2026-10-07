# Screenshot Plan

本次没有伪造或用设计稿冒充实际 UI 截图。已在 `v1.0.0-beta.6` 实际程序中捕获主目录、双击图片预览、中文安全导入向导和“我的部件库”来源状态，并保存到 `docs/images/`。其余截图仍按下列位置补充：

| Screenshot | 操作位置 | 必须显示 | DPI |
|---|---|---|---|
| Main Catalog（已完成） | 启动后 Catalog | EDZ Library、结果表、部件详情、1.0 Beta | 100% |
| Part Preview（已完成） | 双击部件结果 | 部件编号、真实 EDZ 内嵌图片、资源类型 | 100% |
| My Library（已完成） | My Library | 收藏状态、来源状态和来源缺失提示 | 100% |
| Export Dialog | 选择 Saved Parts → Export Selected EDZ | 选择数、来源、目标、确认边界 | 100% |
| Safe Import Wizard（已完成初始页） | Import Selected to EPLAN Parts DB | 中文八步导航、明确目标路径、Preview 前边界 | 100% |
| EPLAN Context Bar | Add-In Connected 后主窗口顶部 | EPLAN version、project/selection availability；不显示敏感项目路径 | 100% |
| Diagnostics | Environment Diagnostics | Green/Yellow/Red 加文字、EPLAN compatibility、SQLite、Bridge/Add-In | 100% / 200% |

截图前必须使用非敏感测试库；不得显示用户名、真实项目路径、客户名或完整诊断日志。
