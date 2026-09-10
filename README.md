# DshLauncher — DeepSeek Harness 一键启动器

> **Manager transport migration:** dsh-manager now uses one plain HTTP upstream port and does not use private certificates or TLS fingerprints. Use `http://` only on trusted private networks; for public access configure HTTPS/WSS at an external reverse proxy. DSH 0.1.2-rc.1 startup URLs carry a one-time token; DshLauncher keeps it in memory for initial navigation and redacts it from launcher logs.

![Version](https://img.shields.io/badge/version-v0.3.2-blue)
![License](https://img.shields.io/badge/license-MIT-green)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![Size](https://img.shields.io/badge/single%20exe-1.4MB-lightgrey)

**DeepSeek Harness（dsh）桌面启动器**：双击即用，打开 dsh Web UI；支持 SSH 远程连接多台服务器、一键同步本地配置与插件。

## 项目背景

[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（dsh）是 DeepSeek 的 CLI 驱动 AI 开发环境，官方使用方式是「终端启动 + 浏览器访问」两步流程：

- 终端执行 `dsh web` 启动服务，再手动打开浏览器；
- 插件安装 / 卸载 / 更新、日志查看、服务重启全部依赖终端；
- 想用远程服务器的 dsh，只能在服务器上开终端。

**DshLauncher 的目标**：把这些操作装进一个桌面窗口 —— 双击打开 Web UI，托盘管理生命周期，SSH 直连远程服务器，本地配置与插件一键同步到服务器。

## 特性

- **一键启动**：双击即打开 dsh Web UI（自动连接已有实例或后台启动新服务）
- **单文件交付**：单个 exe 约 1.4 MB，绿色免安装，不捆绑 Node / Electron / dsh 内核
- **托盘常驻**：关闭窗口隐藏到托盘、服务保持运行；重启 / 日志 / 插件 / 设置 / 更新 dsh 全在托盘
- **插件管理**：可视化安装 / 卸载 / 更新插件，无需终端；支持导出带实际版本的 JSON 清单，并在其他实例按版本导入安装
- **启动诊断**：dsh 启动失败时显示进程退出码以及 stdout / stderr 诊断输出
- **版本信息与更新检查**：关于窗口显示 launcher 项目链接、当前 dsh 实例版本，并可同时检查 DshLauncher 与 dsh 最新版本
- **静默托盘**：窗口隐藏到托盘时不发送系统通知
- **SSH 远程（多服务器）**：本地窗口 + 远程 dsh —— 基于系统 OpenSSH，支持密钥 / 密码认证、从 `~/.ssh/config` 导入主机、每个服务器独立窗口
- **配置与插件同步**：本地 dsh 配置（`settings.yaml` 等）与已装插件一键同步到服务器，不用逐个重装
- **dsh-manager Agent**：可注册到自托管 Go manager，统一上报本地与 SSH 实例状态，并接受远程启动 / 停止 / 重启 / 同步 / 更新命令
- **原生 dsh-manager 前端**：在 Launcher 内登录 manager，查看 Agent / dsh 实例、执行生命周期命令，并用独立 WebView2 窗口打开 manager 代理的远程 dsh；不依赖 Dashboard，不共享本地或 SSH 会话 Cookie
- **dsh 直连插件**：不使用 launcher 时，可在 dsh 内安装 [dsh-manager-plugin](https://github.com/NevermindZZT/dsh-manager-plugin)，直接建立 manager 反向连接
- **Agent 通道**：manager 使用单一 HTTP/WS 端口；公网 HTTPS/WSS 由外部反向代理终止，Agent Token 使用 Windows DPAPI 保护
- **快捷键**：`Ctrl+Shift+R/L/P/S/M/Q/C/Y` 覆盖重启 / 日志 / 插件 / 设置 / Manager / 退出 / 连接 / 同步（Ctrl+Shift 组合避免与页面快捷键冲突）

## 类似项目对比

| 方案 | 技术栈 | 体积 | SSH 远程 | 说明 |
|---|---|---|---|---|
| **DshLauncher（本方案）** | C# WinForms + WebView2 | **约 1.4 MB 单文件** | ✅ 系统 SSH，多服务器多窗口 | 轻量、免终端、配置/插件同步 |
| [deepseek-harness-desktop](https://github.com/anywhere-labs/deepseek-harness-desktop) | 桌面 Web 容器 | 较大 | ❓ | DSH 生态桌面端，功能更重 |
| 官方流程（`dsh web` + 浏览器） | CLI + 浏览器 | 0 | ❌ | 基础用法，依赖终端，无管理能力 |
| VS Code Remote-SSH（模式参考） | 桌面 + SSH | 大 | ✅ | 本地 UI + 远程环境（dsh 无此方案，DshLauncher 提供类似体验） |

**设计取向**：DshLauncher 坚持「最小依赖、小体积、不做多余功能」—— 不引入账号体系、插件市场、云同步等重功能，聚焦「打开、管理、远程、同步」。

## 使用方法

### 1. 安装

- 从 **Releases** 下载 `DshLauncher.exe`（单文件，绿色免安装）；
- 需要本机安装 dsh：`npm install -g @deepseek-ai/dsh`（启动器首次运行也会引导安装）；
- 需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows 10/11，部分系统已自带）。

### 2. 本地使用

双击 `DshLauncher.exe` → 自动打开 dsh Web UI。窗口右上角关闭即隐藏到托盘（服务保持运行），托盘菜单管理一切。

### 2.1 插件列表迁移

在主窗口打开「工具 → 插件管理」：

1. 点击「导出列表」，选择 JSON 文件保存当前插件、声明规格和实际安装版本；
2. 在另一台 DshLauncher 打开同一菜单，点击「导入列表」选择 JSON；
3. 确认后，启动器逐项执行 `dsh plugin --profile web add <包名>@<版本>`，内置 dsh 模板自动跳过；
4. 全部安装完成后 launcher 只重启一次 dsh，使插件生效。

清单仅保存插件包名与版本信息，不包含账号、Token 或 dsh 会话数据。

在「关于」窗口点击「检查更新」会同时检查 DshLauncher 和 dsh：DshLauncher 有新版本时提供 GitHub 直接下载链接，dsh 有新版本时可确认后自动执行 npm 更新并重启实例。

### 3. SSH 远程连接

对于 DSH 0.1.2-rc.1，launcher 会捕获远端 `dsh web:` 启动 URL 中的 token，并只在当前内存会话中改写为本地转发地址；日志会隐藏 token。旧的、已运行但没有可恢复 startup token 的远端 dsh 需要先停止后重新由 launcher 启动。

1. **准备服务器**：安装 Node.js 与 dsh（`npm install -g @deepseek-ai/dsh`）
2. **添加连接**：设置 → SSH 连接 → 新增（或从「系统 SSH 配置」导入 `~/.ssh/config` 已有主机）
   - 认证：推荐密钥（点「生成密钥」→「复制公钥」粘贴到服务器 `~/.ssh/authorized_keys`），或直接填密码
   - 本地端口留 0（自动分配）
3. **测试连接**：设置里「测试连接」→ 显示 SSH 正常 + 远端 dsh 版本
4. **连接**：`Ctrl+Shift+C` 打开连接选择器 → 选择服务器 → 独立窗口打开远程 dsh
5. **同步**：SSH 窗口按 `Ctrl+Shift+Y`，把本地配置（`settings.yaml` 等）与已装插件同步到服务器，完成后可选重启远端生效
6. **打开远端文件夹**：SSH 窗口按 `Ctrl+Shift+O`（或直接点 dsh UI 的「工作区 +」按钮，会被自动拦截）→ 弹出服务器目录浏览器 → 选择目录即添加到远端 dsh 工作区 → 刷新后即可打开

### 4. dsh-manager 远程管理

1. 在服务器运行 dsh-manager，默认 manager HTTP 端口为 `8080`（Docker Compose 示例使用 `10090`）；
2. 打开启动器设置，启用「dsh-manager Agent」；
3. 可信内网填写 `http://manager.example.com:8080`；公网填写外部 HTTPS 反向代理地址，例如 `https://manager.example.com`；
4. 不需要配置 TLS 指纹。首次注册时填写 Agent 名称和 pairing code，配对成功后后续连接只使用 Agent Token；
5. 保存并重启启动器，launcher 会自动注册并保持 Agent 长连接；
6. manager 通过 `/api/v1/instances/{agentId}/{instanceId}/commands` 可以下发 `start`、`stop`、`restart`、`sync`、`update` 命令。

Agent Token 配对成功后会由 Windows DPAPI 加密保存，不会以明文写入 launcher 设置文件。

#### 原生 Manager 前端

点击标题栏「Manager」、托盘菜单「dsh-manager」或按 `Ctrl+Shift+M`，可在 Launcher 内直接登录 manager。原生面板支持：

- 查看 Agent 在线状态、平台和版本；
- 查看每个 Agent 的 dsh 实例状态、版本和错误；
- 下发启动、停止、重启、同步和更新命令；
- 点击「打开 dsh」在独立 WebView2 窗口中打开 manager 返回的 `/dsh/<session>/` 代理地址；
- manager 登录会话使用 HttpOnly `dsh-session` Cookie，Launcher 只将 DPAPI 保护后的会话材料保存到本机；
- 每个 manager 实例使用独立 WebView2 user-data profile，避免与本地 dsh、SSH dsh 或其它 manager 实例串 Cookie。

原生面板不嵌入 manager Dashboard，也不抓取 Dashboard DOM。公网部署仍必须使用 HTTPS/WSS 反向代理；manager 的 `http://` 只适合可信内网。

### 5. 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+Shift+R` | 重启当前连接 |
| `Ctrl+Shift+L` | 日志 |
| `Ctrl+Shift+P` | 插件管理 |
| `Ctrl+Shift+S` | 设置 |
| `Ctrl+Shift+M` | dsh-manager 原生面板 |
| `Ctrl+Shift+C` | 连接选择器（本地 + 各服务器） |
| `Ctrl+Shift+Y` | 同步本地配置与插件到当前服务器 |
| `Ctrl+Shift+O` | 打开远端文件夹（目录浏览器 → 添加到工作区） |
| `Ctrl+Shift+Q` | 退出 |

## 构建

本项目独立仓库：

- launcher：[github.com/NevermindZZT/dsh-launcher](https://github.com/NevermindZZT/dsh-launcher)
- manager：[github.com/NevermindZZT/dsh-manager](https://github.com/NevermindZZT/dsh-manager)

需要 .NET 8 SDK（Windows）：

```powershell
git clone https://github.com/NevermindZZT/dsh-launcher
cd dsh-launcher
.publish-single.ps1    # 产出 distDshLauncher.exe 单文件
```

## 开源协议

[MIT](LICENSE) © DshLauncher contributors

---

*与 DeepSeek Harness（dsh）无隶属关系；dsh 是 DeepSeek 的独立开源项目。*
