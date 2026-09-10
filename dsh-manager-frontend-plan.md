# DshLauncher 原生 dsh-manager 前端实施计划

> 目标版本：v0.4.0
> 当前版本：v0.4.0
> 当前实施范围：原生 Manager 面板与远程 dsh 实例窗口
> 明确不纳入本阶段：Agent 权限请求/询问的最上层浮窗确认

## 总体目标

让 DshLauncher 直接作为 dsh-manager 的管理员前端：在 Launcher 内完成 manager 登录、Agent/实例查看、实例生命周期操作，并在独立 WebView2 窗口中打开 manager 代理的远程 dsh。实现不采用嵌入 Dashboard 的过渡方案，不抓取 Dashboard DOM，也不把 manager 管理功能交给外部浏览器。

## 阶段一：Manager 管理客户端与安全会话

1. 新增 manager 管理客户端，复用 manager 现有 HTTP API：登录、注销、Agent 列表、实例列表、命令下发、打开实例。
2. 支持 manager 地址校验，只允许用户配置的 HTTP/HTTPS origin；公网场景要求 HTTPS/WSS 由反向代理提供。
3. 登录使用 manager 的 session cookie；客户端不记录密码，持久化会话材料使用 Windows DPAPI 保护，或在退出时清除。
4. 处理 401/403、网络断线、manager 重启、请求超时和重复提交；不把 Cookie、token、startup URL 写入日志。
5. 抽象稳定的 DTO 与 action/payload 协议，避免 UI 依赖 HTTP 文本或 Dashboard DOM。

## 阶段二：原生 Manager 面板

1. 在 Launcher Web Modal 中增加独立的 manager 页面，使用 Launcher 自己的 HTML/CSS 和 WebMessage Bridge。
2. 增加 Manager 登录/连接状态、刷新、注销入口。
3. 展示 Agent 列表：名称、平台、在线状态、版本、最后心跳和配对状态。
4. 展示实例列表：Agent、实例名称、运行状态、服务可用性、版本和最近错误。
5. 为实例提供启动、停止、重启、同步、更新和打开 dsh 操作；操作按钮显示进行中、成功、失败状态。
6. 支持空列表、离线 Agent、无权限、过期会话和 manager 不可用状态；不能留下“正在加载…”占位弹窗。
7. 遵循现有 Web Shell/Modal 设计规范，支持中英文、深浅主题、窄窗口和 ESC/遮罩关闭。

## 阶段三：远程 dsh 原生窗口

1. manager 返回实例代理 URL 后，由 Launcher 创建独立 ManagerConnectionWindow，不在 Dashboard 内打开。
2. 每个 manager 实例使用独立 WebView2 user-data profile，隔离 manager 管理会话、本地 dsh、SSH dsh 和其它远程实例 Cookie。
3. 只允许打开已配置 manager origin 下的 /dsh/<session>/ 路径，以及该实例所需的同源资源/WebSocket；外部链接仍按 Launcher 规则处理。
4. 复用现有 WebShell、标题栏、日志、关于和窗口生命周期能力，但不把远程 manager 实例伪装成本地连接。
5. 支持多个远程实例并行打开、重复激活已有窗口、窗口关闭后的会话清理和 manager 连接失败提示。
6. 保留 manager 代理返回的 dsh HTTP/WebSocket 行为，不自行实现 Agent tunnel、startup token 或 Cookie 转发。

## 阶段四：可靠性、安全与兼容

1. 增加 manager API mock/集成测试：登录、列表、命令、open、401、超时、重复操作和会话失效。
2. 验证 Cloudflare Tunnel/HTTPS/WSS、HTTP 私网地址、manager 重启和 Agent 离线场景。
3. 检查敏感数据：密码、session cookie、admin token、startup URL、远程 dsh Cookie 不进入普通设置、日志、错误弹窗或 release artifact。
4. 更新 README、设置说明、关于页面和版本兼容说明。
5. 完成本地 Release build、single-file publish、运行时 smoke test 和 WebModal Ja... (line truncated to 2000 chars)

## 当前实施进度

- 计划已保存到本文件。
- 已完成阶段一至三的首个实现闭环：管理员 API 客户端、DPAPI 保护的 manager session、原生 Manager Web Modal、Agent/实例列表、生命周期命令、独立 manager 远程 dsh WebView2 窗口、Cookie/profile 隔离。
- 已完成 README 使用说明和快捷键入口。
- 已完成本地 build、test、WebModal/WebShell JavaScript syntax check、single-file publish，以及 dsh-manager Go API compatibility tests。
- 已完成版本号单独提交、master 与 v0.4.0 tag 推送、GitHub Actions Release 和 DshLauncher.exe 下载资产验证。
- Agent 权限请求/询问浮窗保持后续阶段，当前不实施。
