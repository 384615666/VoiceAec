# VoiceAec

本地实时回声消除(AEC)桌面小工具:把麦克风信号与音箱参考回采一起送入 [LocalVQE](https://github.com/localai-org/LocalVQE) 神经网络模型,消除对方听到的游戏/媒体回声后,输出到 VB-CABLE 虚拟声卡。QQ / Oopz 等语音软件选择 **CABLE Output** 作为麦克风即可获得干净的语音,游戏声音照常外放。

## 开源许可

应用代码以 [MIT](LICENSE) 授权,发布包内第三方组件(推理引擎/模型/ggml/运行库等)见 [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md)。

## 官网与手册

- **官网**: <https://384615666.github.io/VoiceAec/>
- **图文使用手册**: <https://384615666.github.io/VoiceAec/manual.html>

源码分别在 [docs/index.html](docs/index.html) 与 [docs/manual.html](docs/manual.html)。手册对应 v0.4.9;更新版本后界面可能略有差异。

## 下载即用

从 [Releases](../../releases) 下载(**自包含,免 .NET 运行时,含全部模型**):

| 包 | 说明 |
|---|---|
| `VoiceAec-v*-setup-x64.exe` | **推荐**。安装版:每用户安装、无需管理员,自动创建开始菜单/桌面快捷方式,可在"设置 → 应用"里卸载,升级时自动结束旧实例。 |
| `VoiceAec-v*-win-x64.zip` | 便携版:解压到任意目录,双击 `VoiceAec.exe`;配置写在 exe 旁 `voiceaec.json`,适合放 U 盘或绿色使用。 |

两种包功能一致;安装版与便携版不要同时运行(单实例会互相激活)。

前置条件(目标机器需要自备,与本工具无关):

1. **VB-Audio Virtual Cable**(虚拟声卡,免费)。**安装版会自动安装**（向导里取消勾选即可跳过）；便携版首次运行会在面板顶部提示，一键即可安装。装完需重启电脑，重启后"播放设备"里会出现 `CABLE Input`。
2. Windows 10/11 自带 WebView2 运行时(一般都有)

**设备自动适配**:首次启动自动选系统默认通信麦克风 + 默认播放设备(参考回采),输出自动找 CABLE Input——换任何电脑开箱即用。设备不对?在面板电平卡上的下拉里直接换,热切换不停引擎,选择持久保存在 exe 旁 `voiceaec.json`。

语音软件(QQ / Oopz)里把麦克风设备选成 **CABLE Output**;游戏/语音的播放设备走音箱。想开机自动后台运行:面板顶栏打开"开机自启"。

## 产品形态

- **原生桌面窗口**(WinForms + WebView2 承载控制面板,1280×800 一屏)
- 点窗口 **X** → 隐藏到系统托盘继续运行(音频不中断)
- **唯一退出入口**:托盘图标右键 → 退出
- 托盘左键 → 打开/聚焦窗口;右键菜单为自绘的马卡龙风格面板(圆角、彩色线性图标、状态胶囊、自启开关):打开控制面板 / 暂停并静音 / 处理模式四选一 / 开机自启 / 退出
- **开机自启**(可选,面板开关或托盘勾选):注册 HKCU Run 键,开机以 `--tray` 静默后台运行,无需手动启动
- 底栏提示附「打开系统声音设置」按钮，直接跳到 Windows 的「设置 → 系统 → 声音」，方便确认麦克风与扬声器。
- **设备缺失自动回退**：保存的麦克风/参考/输出被拔掉时不再崩溃，自动改用系统默认设备并在底栏提示；保存的选择不覆盖。重新选好设备或点「重新检测」后提示会立即清除。所有设备都不可用时仍以暂停状态打开面板，接好设备后可重新选择并恢复。
- **缺虚拟声卡时默认暂停**：找不到 CABLE 时不会把处理后的麦克风播到物理扬声器（避免意外监听/啸叫）；此时点「恢复处理与输出」也会被后端拒绝，必须在输出下拉里明确选择设备（或在 CABLE 装好后重新检测/重启）。
- **窗口位置与大小**：关闭/退出时记住，下次在原来的位置和尺寸打开（副屏也适用）。
- **记住处理模式**：在面板或托盘切换模式后写入 `voiceaec.json`，下次启动/开机自启自动恢复；命令行 `--model` 只覆盖本次启动，不写入配置。
- **暂停并静音**：输出卡或托盘菜单可停止全部采集、推理与输出；恢复时沿用设备和模式，清空旧音频并重置模型状态。此功能与 OFF 原声直通不同。暂停状态会写入配置，下次启动保持暂停；恢复后沿用当前设备与模式。
- 高 DPI 使用 PerMonitorV2；面板隐藏或最小化后暂停前端轮询并尝试挂起 WebView2。正常电平刷新维持 500 ms，暂停时状态轮询为 2 秒。
- 单实例启动：重复运行只激活已有控制面板，避免两个实例混音。
- **虚拟声卡安装在后台进行**：安装期间面板、电平、暂停按钮不受影响，按钮会显示进度。
- **局域网访问**：面板默认对同一 Wi-Fi / 局域网开放（仅私有网段，无密码），「关于」里会显示 `http://192.168.x.x:8765/`，手机、平板直接打开即可看电平、切模式。安装时自动注册 URL ACL 和防火墙规则；便携版需以管理员身份运行一次安装器或手动授权。不需要可在启动参数加 `--no-lan`。
- **虚拟声卡引导**：没装 VB-CABLE 时启动不会失败，面板顶部出现引导条，可一键安装官方驱动（需管理员授权，装完重启）。
- **关于**：点面板左上角图标/名字，或托盘菜单「关于 VoiceAec」，查看版本、开源许可(MIT)、第三方声明与项目链接；外部链接用系统浏览器打开。

## 一键安装 / 卸载 OBS 插件

完整整合包只需要下载一份。面板顶栏的 **OBS 插件** 可打开管理窗口：

1. 退出 OBS，点击 **一键安装到 OBS**，按系统提示授予管理员权限。
2. 安装器使用 OBS 普通安装版的标准路径 `%ProgramData%\obs-studio\plugins\obs-localvqe`，校验并安装插件、模型和依赖。已有版本先备份到 `%ProgramData%\VoiceAec\ObsPlugin\backups`。
3. 重启 OBS，在麦克风的「滤镜 → 音频滤镜」中添加 **VoiceAec**，配置参考音频源。插件独立工作，可暂停桌面端以避免重复处理。
4. 点击 **一键卸载** 会移除本程序安装的版本；如果安装前已有插件，则恢复备份。后来增加的模型、配置和外部改动文件会保留。不会自动卸载没有本程序安装记录的插件。

OBS 正在运行时禁止安装 / 卸载，不会自动关闭 OBS。便携模式不加载上述标准目录，当前一键安装仅适用于普通安装版 OBS 64 位。安装管理在弹窗打开时按需读取状态，不增加正常面板轮询或音频处理负载。

制作整合发布包：

```powershell
./tools/Package-Windows.ps1 -OutputDirectory <发布目录> `
  -NativeBin <LocalVQE原生构建bin目录> -PluginDll <新obs-localvqe.dll> `
  -ModelsDirectory <模型目录> -LocaleDirectory <插件locale目录> `
  -RuntimeDirectory <MinGW运行库目录>
```

打包脚本会生成安装清单及 SHA256，并复用桌面包内的模型与原生库。普通 `dotnet publish` 不会自动生成 OBS 资源清单，缺少资源时安装入口会提示使用完整整合包。

## 面板功能

- 电平三卡:麦克风 / 参考回采(播放中/静默状态,可换设备)/ 输出,-60..0 dBFS 渐变条 + 峰值保持
- **第二参考(噪声参考)**:参考回采卡内可选第二只麦克风放在旁人/噪声源附近,帮助消除网吧、宿舍里其他人的说话声(建议配 v1.3);也可选第二个播放设备
- 模式四卡热切换:
  | 模式 | 说明 |
  |---|---|
  | OFF | 直通省电,但外放串音全传对方 |
  | v1.2 · 均衡 | 推荐默认,程序 CPU 约 3%(原测试机器) |
  | v1.3 · 最强消除 | 最彻底但轻微削人声,程序 CPU 约 7%(原测试机器) |
  | v1.4 · 纯回声消除 | 仅消回声；v0.4.0 修正数值稳定性，实际效果需重新试听 |
- 运行统计:处理时长 / 累计推理跳数 / 实时速率(正常值 62.5 跳/秒)/ 参考静默跳数 / 输出补零 / 降级直通
- HTTP API(默认 `http://localhost:8765`,占用自动 +1 到 8774；仅允许 loopback 和本地 Host/Origin。所有 `/api/*` 请求需要 `X-VoiceAec-Token`，面板会自动携带当前会话令牌):
  - `GET /api/status` — 全量状态 JSON(含 `autostart`)
  - `GET /api/devices` — 枚举输入/回采/输出设备 + 当前选择 + `ref2Choices`(第二参考可选项,含麦克风)
  - `POST /api/devices` — body JSON `{mic|ref|ref2|out: "设备名"}`(可多字段),热切换并持久化;ref2 也接受另一只麦克风(噪声参考)或 "none"
  - `POST /api/model` — body 纯文本 `off` / `v1.2` / `v1.3` / `v1.4`
  - `POST /api/autostart` — body 纯文本 `true` / `false`
  - `POST /api/pause` — body 纯文本 `true` 暂停并静音，`false` 恢复；失败保持静音并返回错误
  - `GET /api/obs-plugin` — 插件安装状态、OBS 是否运行及最近任务结果
  - `POST /api/obs-plugin/install` / `POST /api/obs-plugin/uninstall` — 异步启动安装 / 卸载，返回 202；管理员授权只在点击后请求
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
- 配置优先保留绿色包模式；exe 旁无法写入时自动落到 `%LocalAppData%\VoiceAec\voiceaec.json`。成功切换的设备独立持久化，失败设备保留旧绑定。

验证（不启动音频设备）：

```powershell
dotnet run --project tests/VoiceAec.Tests.csproj -c Release
node tests/frontend.test.cjs
```

v0.4.0 的原生修复保存为 [native/LocalVQE-windows.patch](native/LocalVQE-windows.patch)，
应用与构建说明见 [native/README.md](native/README.md)。v1.4 的根因、保留旧基准的方式和
验证边界见 [数值修复记录](docs/V14-AEC-NUMERICS.md)；发布说明见 [v0.4.0](docs/RELEASE-v0.4.0.md)。

## 命令行参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `--mic` | 智能默认(见上) | 麦克风设备(子串匹配,优先级高于 voiceaec.json) |
| `--ref` | 智能默认 | 参考回采设备(render loopback) |
| `--ref2` | none(配置可存) | 第二参考(找不到自动降级单参考;`none` 关闭) |
| `--out` | CABLE Input | 输出设备 |
| `--threads` | 2 | 推理线程数 |
| `--model` | 上次保存的模式 | 只覆盖本次启动，不写入配置 |
| `--port` | 8765 | HTTP 端口 |
| `--tray` | — | 静默托盘启动(开机自启用;隐含不开窗口) |
| `--console` | — | 调试:保留控制台窗口 |
| `--list` | — | 列出音频设备后退出 |
| `--no-lan` | — | 只监听本机，不对局域网开放面板 |

## 已知限制

- 摄像头麦克风同一时刻只能被一个程序独占(与 OBS 互斥;两者需要共用麦克风时可参考上游 LocalVQE 仓库的 repoint 思路)
- QQ 对 CABLE Output 报"加载失败"是 QQ 侧对虚拟设备的打开方式问题(音频链路本身已验证正常)
