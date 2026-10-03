# UEFormat 模型查看器 · DeltaModelViewer

读取《三角洲行动》的 UE 模型包（`.uemodel`）并实时渲染：D3D11 视口、轨道相机、贴图与缩略图、模型导出。
界面与「三角洲过场动画查看器 / StringTables 归类浏览器 / 音频工坊」共用同一套设计系统。

- 详细使用说明：[使用说明.md](使用说明.md)
- 应用源码：DeltaModelViewer/ ｜ 模型解析：DeltaCore/ ｜ 自检工具：Tools/VerifyModels/

## 本仓库包含 / 不包含

**包含**：`DeltaModelViewer/`（WinUI 3 应用源码）、`DeltaCore/`（模型解析与网格构建）、`Tools/VerifyModels/`（自检工具）、`Installer/*.iss`（Inno Setup 脚本）、`使用说明.md`。

**不包含**：

- `Installer/App/`、`Installer/AlphaApp/`、`Installer/*.exe`、`Installer/*.7z`：打包暂存与成品安装包（含模型/贴图数据，单个 1.9GB，GitHub 也会拒收 >100MB 文件）—— 安装包见 **Releases**。
- 游戏模型与贴图数据（你来指向自己游戏目录里的素材）。

## 构建

```powershell
dotnet publish DeltaModelViewer\DeltaModelViewer.csproj -c Release -r win-x64 -p:SelfContained=true
dotnet publish DeltaModelViewer\DeltaModelViewer.csproj -c Alpha   -r win-x64 -p:SelfContained=true
```

（`Alpha` 配置启用 NTP 定时过期校验：2026-09-24 北京时间过期。）

## 许可证

MIT（见 [LICENSE](LICENSE)）。第三方组件：Windows App SDK（MIT）、Vortice.Windows（MIT）。