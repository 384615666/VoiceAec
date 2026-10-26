# v0.4.7 · 去掉启动时的命令行窗口

## 下载

| 文件 | 说明 |
|---|---|
| `VoiceAec-v0.4.7-setup-x64.exe` | **推荐**。安装版。 |
| `VoiceAec-v0.4.7-win-x64.zip` | 便携版。 |

## 本版更新

- **修复：双击启动时不再额外弹出黑色命令行窗口。**
  之前程序是控制台子系统，启动会先创建终端，再靠 `ShowWindow` 隐藏；在 Windows 11 把终端设为 Windows Terminal 后，这个隐藏经常失效，于是会看到「日志窗口 + 面板」两个窗口。
  现在改为 GUI 子系统（WinExe），正常启动完全不创建控制台。
- `--console`（调试保留日志）与 `--list`（列出音频设备）仍然可用：带这两个参数时会按需附加父控制台或新建一个。
- 其余与 v0.4.6 一致（局域网访问、系统声音设置入口、关于面板、默认安装 VB-CABLE）。

## 校验

```
SHA256 (VoiceAec-v0.4.7-setup-x64.exe) = afe22a2e9a0b73887f1613fcdf79314f25e49dddbcde449defd53578e8ffc23d
SHA256 (VoiceAec-v0.4.7-win-x64.zip)    = 6b48e35a74c38fcf133cfefd32be01fa0e74c2378e84c96fe8743f97db0f65ed
```
