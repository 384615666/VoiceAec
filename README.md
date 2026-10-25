# VoiceAec · 回声消除麦克风

本地实时回声消除(AEC)桌面小工具:把麦克风信号与音箱参考回采一起送入 [LocalVQE](https://github.com/384615666/obs-localvqe-windows) 神经网络模型,消除对方听到的游戏/媒体回声后,输出到 VB-CABLE 虚拟声卡。QQ / Oopz 等语音软件选择 **CABLE Output** 作为麦克风即可获得干净的语音,游戏声音照常外放。

## 下载即用

从 [Releases](../../releases) 下载 `VoiceAec-v*-win-x64.zip`(**自包含,免 .NET 运行时,含全部模型**),解压到任意目录,双击 `VoiceAec.exe` 即可。

前置条件(目标机器需要自备,与本工具无关):

1. **VB-Audio Virtual Cable**(虚拟声卡,免费;装完后确认"播放设备"里出现 `CABLE Input`)
2. Windows 10/11 自带 WebView2 运行时(一般都有)

语音软件(QQ / Oopz)里把麦克风设备选成 **CABLE Output**;游戏/语音的播放设备走音箱。想开机自动后台运行:面板顶栏打开"开机自启"。

## 产品形态

- **原生桌面窗口**(WinForms + WebView2 承载控制面板,1280×800 一屏)
- 点窗口 **X** 或面板"关闭面板" → 隐藏到系统托盘继续运行(音频不中断)
- **唯一退出入口**:托盘图标右键 → 退出
- 托盘左键 → 打开/聚焦窗口;右键菜单:处理模式四选一 / 开机自启 / 退出
- **开机自启**(可选,面板开关或托盘勾选):注册 HKCU Run 键,开机以 `--tray` 静默后台运行,无需手动启动

## 面板功能

- 电平三卡:麦克风 / 参考回采(播放中/静默状态)/ 输出,-60..0 dBFS 渐变条 + 峰值保持
- 模式四卡热切换:
  | 模式 | 说明 |
  |---|---|
  | OFF | 直通省电,但外放串音全传对方 |
  | v1.2 · 均衡 | 推荐默认,整机 CPU 约 3% |
  | v1.3 · 最强消除 | 最彻底但轻微削人声,CPU 约 7% |
  | v1.4 · 纯回声消除 | 远麦场景实测无效,仅近场尝鲜 |
- 运行统计:处理时长 / 累计推理跳数 / 实时速率(正常值 62.5 跳/秒)/ 参考静默跳数 / 输出补零 / 降级直通
- HTTP API(默认 `http://localhost:8765`,占用自动 +1 到 8774):
  - `GET /api/status` — 全量状态 JSON(含 `autostart`)
  - `POST /api/model` — body 纯文本 `off` / `v1.2` / `v1.3` / `v1.4`
  - `POST /api/autostart` — body 纯文本 `true` / `false`
  - `POST /api/quit` — 退出进程(调试后门)

## 构建

```
dotnet build -c Release
```

- TFM `net10.0-windows`(WinForms),依赖 NuGet:NAudio 2.2.1、Microsoft.Web.WebView2
- **原生依赖需手动放入输出目录** `bin/Release/net10.0-windows/`(csproj 不管理):
  - `liblocalvqe.dll`(LocalVQE 推理库,来自 LocalVQE 源码树 ggml/build/bin)
  - `ggml*.dll`(ggml CPU 后端全家桶)
  - `libgcc_s_seh-1.dll`、`libstdc++-6.dll`、`libwinpthread-1.dll`(MSYS2 ucrt64 运行时)
- **模型文件**查找顺序:exe 旁 `models\` 目录(Release 包内置,推荐)→ obs-localvqe 插件目录 `C:\ProgramData\obs-studio\plugins\obs-localvqe\data`(与 OBS 插件共用,路径写死于 Program.cs)
- 前端 `wwwroot/index.html` 为单文件零依赖页面,build 时自动拷贝到输出目录

## 命令行参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `--mic` | 2K Web Camera | 麦克风设备(子串匹配) |
| `--ref` | USB HiFi | 参考回采回采设备(render loopback) |
| `--ref2` | G2500 | 第二参考(找不到自动降级单参考;`none` 关闭) |
| `--out` | CABLE Input | 输出设备 |
| `--threads` | 2 | 推理线程数 |
| `--model` | v1.2 | 启动模式 |
| `--port` | 8765 | HTTP 端口 |
| `--tray` | — | 静默托盘启动(开机自启用;隐含不开窗口) |
| `--console` | — | 调试:保留控制台窗口 |
| `--list` | — | 列出音频设备后退出 |

## 已知限制

- 摄像头麦克风同一时刻只能被一个程序独占(与 OBS 互斥;共存方案见 obs-localvqe-windows 仓库的 repoint 脚本思路)
- QQ 对 CABLE Output 报"加载失败"是 QQ 侧对虚拟设备的打开方式问题(音频链路本身已验证正常)
