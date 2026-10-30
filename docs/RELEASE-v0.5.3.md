# v0.5.3 · 全新项目主页与文档更新

## 下载

| 文件 | 说明 |
|---|---|
| `VoiceAec-v0.5.3-setup-x64.exe` | **推荐**。安装版：每用户安装，自动创建开始菜单/桌面快捷方式。 |
| `VoiceAec-v0.5.3-win-x64.zip` | 便携版：解压即用，配置写在 exe 旁 `voiceaec.json`。 |

## 本版更新

- **全新项目主页**：README 重做为完整项目主页——徽章、官网封面、功能图解、真实界面截图、四步快速开始、处理模式对照、OBS 一键安装、FAQ 与已知限制；HTTP API、命令行、配置等进阶内容收进折叠块。
- **新增功能图解**：`docs/images/readme-architecture.svg` 展示「麦克风 + 系统声音回采 → LocalVQE 引擎 → VB-CABLE → 语音软件」的完整链路与 OBS 支线。
- **新增英文概览页**：`README.en.md`，中英文 README 顶部可互相切换。
- **截图格式规范化**：`panel-main.png`、`panel-paused.png`、`panel-ref2-menu.png` 实际是 JPEG 字节，已更名为 `.jpg` 并同步 `docs/manual.html` 引用，避免工具按扩展名解码失败。
- **发布包附带文档素材**：安装版/便携版内新增 `docs/`（README 引用的截图与 v1.4 数值修复记录）和 `native/`（LocalVQE Windows 补丁与构建说明），包内 README 的图片与链接可正常打开。
- 程序功能与 **v0.5.2** 完全一致（无代码行为变更），本版为文档与打包更新。

## 校验

```
SHA256 (VoiceAec-v0.5.3-setup-x64.exe) = 3d640cfc33684246bde4fe78e3edbb7f0997d75573d431142de60b64f88885b9
SHA256 (VoiceAec-v0.5.3-win-x64.zip)    = 71624f240b0b7ad8d6f52d83304c90fa9f22731fcb270ade29ad4c2837a92a60
```

安装包没有商业代码签名，SmartScreen 可能提示「未知发布者」；请先核对文件名与 SHA256，再决定是否放行。
