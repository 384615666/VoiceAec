# 第三方组件声明 / Third-Party Notices

VoiceAec 的应用代码以 [MIT](LICENSE) 授权。发布包（setup / zip）中还包含以下第三方组件：

| 组件 | 发行形式 | 许可证 | 来源 |
|---|---|---|---|
| LocalVQE 推理引擎（`liblocalvqe.dll`、`localvqe-*.gguf` 模型） | 二进制 + 模型 | Apache-2.0 | https://github.com/localai-org/LocalVQE |
| obs-localvqe（OBS 音频滤镜） | 二进制 | Apache-2.0（源码）；二进制与 libobs（GPL-2.0）链接分发 | 同上；本仓库 [native/LocalVQE-windows.patch](native/LocalVQE-windows.patch) 为相对上游的 Windows 适配补丁，对应源码 = 上游源码 + 该补丁 |
| ggml（`ggml*.dll`） | 二进制 | MIT | https://github.com/ggml-org/ggml |
| MinGW-w64 运行库（`libgcc_s_seh-1.dll`、`libstdc++-6.dll`、`libwinpthread-1.dll`） | 二进制 | GCC Runtime Library Exception | https://mingw-w64.org |
| NAudio | 二进制（NuGet） | MIT | https://github.com/naudio-dev/NAudio |
| Microsoft.Web.WebView2（加载器） | 二进制（NuGet） | MIT | https://github.com/MicrosoftEdge/WebView2Samples |
| .NET 运行时（自包含发布） | 二进制 | MIT | https://github.com/dotnet/runtime |
| Inno Setup（安装程序构建工具） | 构建工具 | Inno Setup License | https://jrsoftware.org/isinfo.php |
| VB-CABLE 虚拟声卡（`vendor\VBCABLE`，官方原包） | 二进制（可选安装） | VB-Audio donationware | https://www.vb-cable.com |

VB-CABLE 是 VB-Audio 的捐赠软件（donationware），所有赞助都欢迎。VoiceAec 只随包分发**未经修改**的官方驱动包，用于简化安装；安装时可在向导中取消勾选，也不会在卸载 VoiceAec 时移除该驱动（其它程序可能正在使用）。官网：https://www.vb-cable.com 。

其它需用户自行安装的外部依赖：OBS Studio（https://obsproject.com/）、WebView2 运行时（Windows 10/11 一般自带）。

各许可证全文见上游仓库；Apache-2.0 与 GPL-2.0 的相应义务（保留版权声明、提供对应源码）通过本文件与上表来源链接履行。
