# GitHub Social Preview Guide

Social Preview 必须建立在真实软件截图上。本阶段只确定版式，不生成概念 UI，也不使用 EPLAN 官方 Logo。

## 画布与导出

- 尺寸：`1280×640 px`。
- 格式：PNG；使用 sRGB，确保文字在缩略图中仍清晰。
- 安全区：四周至少留 `48 px`；标题和核心卖点不要贴边。
- 截图来源：真实 `main-window.png`，不能使用 AI 生成的软件界面。

## 推荐版式

```text
┌──────────────────────────────────────────────────────────────┐
│                                                              │
│  EPLAN EDZ Manager                                           │
│                                                              │
│  Fast EDZ Search                                             │
│  Personal Parts Library                                      │
│  Official EPLAN Export                                       │
│                                      ┌─────────────────────┐ │
│  C# · .NET · WPF · SQLite            │ Real UI Screenshot  │ │
│                                      └─────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
```

建议左侧约 42% 放标题和三个卖点，右侧约 58% 放真实界面截图。截图可使用圆角矩形和轻微阴影，但不要修改软件内容，也不要把警告或限制裁掉后制造不同的产品状态。

## 文字与品牌边界

- 使用英文，便于 GitHub、LinkedIn、Reddit 等国际场景显示。
- 只保留 `EPLAN EDZ Manager`、三个卖点和技术栈，不堆叠十几个功能。
- 不使用 EPLAN 官方 Logo、官方产品配色模板或“Official”作为项目身份描述。
- “Official EPLAN Export”描述的是导出路径；画面角落应保留 `Independent, unofficial project` 小字，避免被理解为官方项目。
- 不添加未经证实的性能数字、用户数量或 Star 数。

## 制作步骤

1. 先按 [README Media Checklist](images/README.md) 捕获 `main-window.png`。
2. 在 Figma、PowerPoint、Canva 或其它本地工具建立 `1280×640` 画布。
3. 放入真实截图，不上传包含私人数据的源文件到不受控的在线服务。
4. 导出 `social-preview.png`，缩小到约 320 px 宽预览，确认标题和三个卖点仍可读。
5. 在 GitHub 仓库 **Settings → General → Social preview** 手工上传；该设置不由本次本地文档修改自动完成。

## 上传前检查

- 截图中没有用户名、路径、客户/项目名、许可证、日志、Token、密码或 API Key。
- 没有 EPLAN 官方 Logo，也没有“endorsed”“certified”等未经授权用语。
- 英文拼写、版本和实际 README 一致。
- 分享仓库链接后，重新检查裁切和深色/浅色背景下的识别度。
