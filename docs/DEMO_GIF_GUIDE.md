# Demo GIF Recording Guide

目标是用一段约 20 秒的真实软件画面，证明“能搜索、能预览、能收藏”。不要录概念动画，也不要为了追求完整而把耗时较长的 EPLAN 导出和 MDB 导入流程塞进同一个 GIF。

## 录制前准备

1. 使用准备公开的安装包或 Portable ZIP，确认标题栏/About 显示的版本正确。
2. 准备一个无客户信息的演示库，并提前完成扫描，避免把等待时间录入 GIF。
3. 选择能稳定命中并有图片/宏资源的部件，例如 `CJ2M`；如果真实数据中没有该型号，改用确实存在的型号，不要伪造结果。
4. 清除搜索框和无关弹窗，把窗口固定为 `1600×900` 或 `1920×1080`，Windows 缩放保持 100%。
5. 检查画面中没有用户名、真实目录、客户/项目名、许可证、Token、API Key 或完整日志。

## 20 秒脚本

| 时间 | 操作 | 画面重点 |
|---:|---|---|
| 0–3 s | 打开或切回主界面 | EDZ Libraries、部件列表和详情区域同时可见。 |
| 3–7 s | 输入 `CJ2M` | 搜索响应和结果数量清楚可见。 |
| 7–11 s | 选择一个真实结果 | Part Number、Manufacturer、Description 等元数据可读。 |
| 11–16 s | 打开图片/宏/Resources | 至少展示一个真实存在的预览或资源条目。 |
| 16–20 s | 点击 Favorite 或加入 My Library | 状态变化明确，停留约 1 秒后结束。 |

## OBS 录制

1. 新建场景，仅添加“窗口采集”，目标选择 EPLAN EDZ Manager；不要使用“显示器采集”。
2. 画布和输出分辨率设为 `1920×1080` 或 `1600×900`，帧率 30 FPS。
3. 关闭麦克风和桌面音频；GIF 不需要音轨。
4. 推荐先录制 MKV，结束后用 OBS 的“重新封装录像”转为 MP4。这样即使录制异常中断，也较不容易损坏整段视频。
5. 按脚本录 2–3 次，选择鼠标移动最稳、无错误弹窗且搜索结果真实的一次。

## GIF 转换与压缩

优先保留一份 MP4 作为演示视频；README GIF 建议宽度 `1200–1440 px`、12–15 FPS，并尽量控制在约 10 MB 以内。文字模糊时，先缩短时长或裁掉空白区域，不要一味降低清晰度。

如果本机已有 FFmpeg，可使用两步调色板流程：

```powershell
ffmpeg -i demo.mp4 -vf "fps=15,scale=1280:-1:flags=lanczos,palettegen=stats_mode=diff" palette.png
ffmpeg -i demo.mp4 -i palette.png -lavfi "fps=15,scale=1280:-1:flags=lanczos[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=3" docs/images/demo-search.gif
```

也可以用 ScreenToGif 手工裁剪停顿、降低重复帧并导出。无论使用哪种工具，都要回放检查搜索文字、部件型号和 Favorite 状态仍然清楚。

## 提交前检查

- GIF 是真实软件录制，不是设计稿或 AI 生成画面。
- 画面没有跳转到私人目录，也没有通知弹窗。
- 不展示尚未实现的功能或把 `Unavailable` 伪装为成功。
- 文件名固定为 `docs/images/demo-search.gif`。
- 在 GitHub README 预览中检查尺寸、首帧、循环和加载速度。
