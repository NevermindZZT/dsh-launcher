# DshLauncher — DeepSeek Harness 一键启动器

Windows 上的 DeepSeek Harness（dsh）桌面启动器：**双击即用**，内置 WebView2 窗口直接打开 dsh Web UI。

## 项目背景

DeepSeek Harness 官方使用方式是「终端启动 + 浏览器访问」两步流程：

- 在终端执行 `dsh web` 启动本地服务，再手动打开浏览器访问 `http://127.0.0.1:3080`；
- 插件安装 / 卸载 / 更新需要回到终端执行 `dsh plugin ...`，并重启服务才生效；
- 服务的启动、查看日志、重启都依赖终端，桌面使用体验割裂。

## 项目目标

做一个极简的 Windows 启动器，坚持四条原则：

1. **一键启动**：双击即打开 dsh Web UI —— 自动连接已有实例（attach），或后台隐藏启动服务（spawn）并接管生命周期；
2. **最小依赖**：不捆绑 Node.js / Electron / dsh 内核 —— dsh 保持官方原生安装（`npm install -g @deepseek-ai/dsh`），渲染复用系统 WebView2 Runtime，运行复用 .NET 8 Desktop Runtime；
3. **小体积**：**单个 exe 约 1.4 MB**，绿色解压即用；
4. **不做多余功能**：只提供启动、重启、日志、插件管理、设置等必要操作，不引入账号体系、插件市场、云同步等重功能。

## 特性

- **单文件交付**：`dist\DshLauncher.exe`（约 1.4 MB，零依赖文件）
- **双击即用**：自动 attach 已有 dsh 实例（默认 `127.0.0.1:3080`）或隐藏启动新服务（`--port 0` 由系统分配）
- **内置窗口**：WebView2 渲染 dsh UI，窗口标题实时跟随网页动态标题（会话名）
- **托盘常驻**：关闭窗口隐藏到托盘、服务保持运行（秒开）；托盘菜单：打开主窗口 / 重启宿主 / 日志 / 插件管理 / 设置 / 更新 dsh / 退出
- **快捷键**：`Ctrl+R` 重启宿主 · `Ctrl+L` 日志 · `Ctrl+P` 插件管理 · `Ctrl+,` 设置 · `Ctrl+Q` 退出
- **日志窗口**：实时查看宿主 stdout/stderr（落盘 + 自动轮转）
- **插件管理**：列装已装插件，安装 / 卸载 / 更新走官方 `dsh plugin`，操作后自动重启宿主
- **安装与更新引导**：未安装 dsh 时一键安装；启动后自动检查新版本，托盘一键升级
- **Win11 风格界面**：Mica 材质 + 自适应系统深浅主题 + Fluent 控件（圆角按钮 / 输入框 / 自绘复选框单选）
- **单实例**：Mutex 防止并发启动导致 dsh 配置冲突
- **安全默认**：仅绑定 loopback、WebView 导航守卫、权限最小化

## 快速开始

1. 安装 Node.js 与 dsh（一次）：`npm install -g @deepseek-ai/dsh`（未安装时启动器会引导一键安装）
2. 运行 `dist\DshLauncher.exe`
3. （可选）托盘 → 设置 → 勾选「开机自动启动」

## 依赖

| 组件 | 说明 |
|---|---|
| Windows 10 / 11 | Win11 22H2+ 体验最佳（Mica 材质）；Win10 自动降级深色纯色 |
| .NET 8 Desktop Runtime | 未安装时需从微软官网安装（约 30 MB） |
| WebView2 Runtime | Win11 已内置；Win10 通常随 Edge 安装，缺失时启动器引导安装 |
| Node.js + dsh（npm 全局） | dsh 官方安装方式；启动器只复用，不捆绑 |
| pnpm（可选） | 仅插件管理功能需要 |

## 构建

```powershell
# 一键发布单文件 exe（直接输出到 dist\DshLauncher.exe）
& .\publish-single.ps1
```

构建环境：.NET 8 SDK（离线场景可在 `.tools\dotnet` 放置，git 已忽略）+ NuGet 源（离线场景在 `.tools\nuget` 放本地包，正常网络可直接用官方源）。

## 目录结构

```
DshLauncher/
├── src/                      # 源码（C# / WinForms / WebView2）
│   ├── MainForm.cs           # 主窗口：WebView2 + 托盘 + 快捷键 + 主题
│   ├── HostSupervisor.cs     # dsh 宿主进程管理（spawn/attach/就绪行/Job 终止）
│   ├── PluginManager.cs      # 插件管理（列装 + dsh plugin 转发）
│   └── ...（设置/日志/主题/更新等）
├── dist/                     # 交付物：DshLauncher.exe（单文件，由发布脚本生成）
├── publish-single.ps1        # 单文件发布脚本（输出到 dist）
├── NuGet.config              # 构建用 NuGet 源配置
└── README.md
```

## 技术要点

- **进程生命周期**：attach 探测（HTTP + 标记识别）或 spawn（解析 stdout 就绪行 `dsh web: http://127.0.0.1:<port>`）；Job Object 保证进程树整体清理；导航失败自动重试
- **主题**：DWM Mica + 深/浅色标题栏跟随系统主题；子窗口自绘 Fluent 控件（`ThemedForm` 基类统一调色板）
- **安全**：只放行 loopback 导航，外部链接交系统浏览器；WebView 权限默认拒绝（放行剪贴板）
- **图标**：DeepSeek 官方白色鲸鱼（提取自官方资源重着色，多尺寸嵌入）

## License

MIT
