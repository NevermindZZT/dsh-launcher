# DshLauncher — DeepSeek Harness 一键启动器

![Version](https://img.shields.io/badge/version-v0.1.0-blue)
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
- **插件管理**：可视化安装 / 卸载 / 更新插件，无需终端
- **SSH 远程（多服务器）**：本地窗口 + 远程 dsh —— 基于系统 OpenSSH，支持密钥 / 密码认证、从 `~/.ssh/config` 导入主机、每个服务器独立窗口
- **配置与插件同步**：本地 dsh 配置（`settings.yaml` 等）与已装插件一键同步到服务器，不用逐个重装
- **快捷键**：`Ctrl+Shift+R/L/P/S/Q/C/Y` 覆盖重启 / 日志 / 插件 / 设置 / 连接 / 同步（Ctrl+Shift 组合避免与页面快捷键冲突）

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

### 3. SSH 远程连接

1. **准备服务器**：安装 Node.js 与 dsh（`npm install -g @deepseek-ai/dsh`）
2. **添加连接**：设置 → SSH 连接 → 新增（或从「系统 SSH 配置」导入 `~/.ssh/config` 已有主机）
   - 认证：推荐密钥（点「生成密钥」→「复制公钥」粘贴到服务器 `~/.ssh/authorized_keys`），或直接填密码
   - 本地端口留 0（自动分配）
3. **测试连接**：设置里「测试连接」→ 显示 SSH 正常 + 远端 dsh 版本
4. **连接**：`Ctrl+Shift+C` 打开连接选择器 → 选择服务器 → 独立窗口打开远程 dsh
5. **同步**：SSH 窗口按 `Ctrl+Shift+Y`，把本地配置（`settings.yaml` 等）与已装插件同步到服务器，完成后可选重启远端生效
6. **打开远端文件夹**：SSH 窗口按 `Ctrl+Shift+O`（或直接点 dsh UI 的「工作区 +」按钮，会被自动拦截）→ 弹出服务器目录浏览器 → 选择目录即添加到远端 dsh 工作区 → 刷新后即可打开

### 4. 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+Shift+R` | 重启当前连接 |
| `Ctrl+Shift+L` | 日志 |
| `Ctrl+Shift+P` | 插件管理 |
| `Ctrl+Shift+S` | 设置 |
| `Ctrl+Shift+C` | 连接选择器（本地 + 各服务器） |
| `Ctrl+Shift+Y` | 同步本地配置与插件到当前服务器 |
| `Ctrl+Shift+O` | 打开远端文件夹（目录浏览器 → 添加到工作区） |
| `Ctrl+Shift+Q` | 退出 |

## 构建

需要 .NET 8 SDK（Windows）：

```powershell
git clone https://github.com/<your-name>/dsh-launcher
cd dsh-launcher
.publish-single.ps1    # 产出 distDshLauncher.exe 单文件
```

## 开源协议

[MIT](LICENSE) © DshLauncher contributors

---

*与 DeepSeek Harness（dsh）无隶属关系；dsh 是 DeepSeek 的独立开源项目。*
