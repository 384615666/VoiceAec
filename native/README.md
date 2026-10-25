# Windows 原生修复源码

`LocalVQE-windows.patch` 保存本次推理库和 OBS 插件的全部源码改动及新增测试基准，避免
桌面端发布时丢失另一个本地目录中的修复。它不包含机器配置、构建产物或模型文件。

基于 https://github.com/localai-org/LocalVQE 的 `7f61cd0`；应用方式：

```powershell
git clone --recursive https://github.com/localai-org/LocalVQE.git
cd LocalVQE
git checkout 7f61cd0
git submodule update --init --recursive
git apply --check /path/to/VoiceAec/native/LocalVQE-windows.patch
git apply /path/to/VoiceAec/native/LocalVQE-windows.patch
```

仓库已有的 `ggml/patches/ggml-gru.patch` 由 CMake 配置时自动应用到固定的 ggml 子模块。
本机原有的该补丁不作为子模块新提交。

已验证的构建环境为 Windows x64、MSYS2 UCRT64 GCC 16.2、CMake/Ninja：

```powershell
cmake -S ggml -B ggml/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DLOCALVQE_BUILD_SHARED=ON
cmake --build ggml/build --parallel 4
cmake --build ggml/build --target regression-assets --parallel 4
ctest --test-dir ggml/build --output-on-failure
```

OBS 插件源码和回归测试也包含在补丁内；插件构建需要匹配的 OBS 64 位开发头文件及导入库。
本次 DLL 使用 OBS 32.2.2 头文件。打包入口为 `tools/Package-Windows.ps1`，接收已构建的
原生运行库和插件 DLL。数值变更的原因及验证边界见 `docs/V14-AEC-NUMERICS.md`。
