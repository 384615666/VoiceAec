# v0.4.1 · 关于面板

## 下载

| 文件 | 说明 |
|---|---|
| `VoiceAec-v0.4.1-setup-x64.exe` | **推荐**。安装版，每用户安装、无需管理员权限。 |
| `VoiceAec-v0.4.1-win-x64.zip` | 便携版，解压即用。 |

均自包含（免 .NET 运行时），已内置全部模型。

## 本版更新

- **新增「关于」**：面板顶栏 ⓘ 按钮与托盘菜单「关于 VoiceAec」均可打开——展示应用图标、版本号（薄荷绿胶囊）、项目简介、开源许可（MIT）、第三方组件声明与 GitHub / 检查更新 / 上游 LocalVQE 链接；外部链接经系统浏览器打开，不离开面板。
- 版本号由 `/api/status` 的 `version` 字段提供（程序集版本），托盘入口会自动唤起面板并展开关于弹窗。
- 其余与 v0.4.0 一致（新图标、自绘托盘菜单、OBS 插件管理、v1.4 数值修复）。

## 升级说明

- 从 v0.4.0 升级：运行安装包即可，安装器会自动结束旧实例。
- 本仓库已开源（MIT）；第三方组件许可见 [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md)。

## 校验

```
SHA256 (VoiceAec-v0.4.1-setup-x64.exe) = c0fc2b5afa82330edbd7758ee6824a0a26e046e7f6ecfd5c7171bed24c58e197
SHA256 (VoiceAec-v0.4.1-win-x64.zip)    = bf22bec09ba09b9c52f3e3b3665e52b8bfe5dbef7b6429a8c11449345dc4d93c
```
