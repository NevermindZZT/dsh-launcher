# DshLauncher Web UI 宿主重构计划

## 目标

将 DshLauncher 的 WinForms 原生窗口入口逐步迁移到当前 WebView2 内的统一 Web Shell，同时保留现有 dsh 页面、WebSocket、Cookie、Manager Agent、SSH 连接和工作区拦截能力。

## 总体原则

- 不使用 iframe，直接在当前 dsh WebView2 页面上注入宿主 UI；
- MainForm 与 ConnectionWindow 共用同一套 Web Shell、标题栏、菜单和 Modal Router；
- 原生 C# 业务逻辑继续复用，Web UI 通过 WebMessage Bridge 调用；
- 先新增 Web UI，再逐步切换入口，最后清理旧 WinForms 窗口；
- 保留托盘、快捷键、单实例和后台生命周期行为；
- 每个 SSH 窗口拥有独立的 Web Shell context，弹窗不得跨母窗口串联。

## 阶段一：Web Shell 与自绘标题栏

1. 新增统一 Web Shell 注入脚本和 CSS；
2. 将 MainForm、ConnectionWindow 改为无系统标题栏；
3. 增加 Web 标题栏、拖动区域、最小化、最大化、还原、关闭按钮；
4. 增加设置、工具、关于三个菜单；
5. 增加 WebMessage Bridge 和窗口控制消息；
6. 监听 dsh document.title；
7. 监听 dsh CSS 主题变量和 class，动态同步标题栏配色；
8. 确保 WebView2 加载、导航、快捷键和现有 dsh 页面不回归。

## 阶段二：Web Modal Router 与设置/关于

1. 新增 Web Modal Router；
2. 将设置拆分为常规、外观、dsh、Manager Agent、SSH、快捷键、高级；
3. 复用 AppSettings、ManagerSettings、ConnectionManager 和现有保存逻辑；
4. 新增关于页面，显示 launcher、dsh、WebView2 和 Manager Agent 信息；
5. 将设置、关于菜单和快捷键切换到 Web 弹窗；
6. 暂时保留 SettingsForm 作为回退代码，但移除主入口调用。



### 阶段二进度（已实现）

- 已新增 `WebModalRouter`：在现有 dsh WebView2 页面内注入共享 Modal Router。
- 设置入口已切换为 Web Modal，包含常规、Manager Agent、SSH 摘要、关于分类；保存继续复用 `AppSettings`、`ManagerSettings`、`ManagerAgent` 和连接同步逻辑。
- Web 菜单与 Ctrl+Shift+S 不再调用 `SettingsForm`；原生 `SettingsForm` 保留作为回退代码。
- 日志、插件、SSH 编辑/连接窗口仍保留原生入口，按阶段计划暂不迁移。

## 阶段三：日志与插件管理 Web 化

1. 将 LogForm 改为 Web 日志弹窗；
2. 支持连接选择、级别过滤、自动滚动、清空和导出；
3. 复用 IDshConnection.LogLine 与 Diag 日志广播；
4. 将 PluginsForm 改为 Web 插件管理弹窗；
5. 支持安装、卸载、更新、启用、禁用、重启和错误状态；
6. 保留原有插件操作业务方法，不在前端重复实现 npm 逻辑。

## 阶段四：SSH Remote Web 化

1. 将 ConnectionPickerForm 改为 Web 弹窗；
2. 将 SshEditForm 改为 Web 表单；
3. 支持新增、编辑、删除、测试和连接；
4. 每个 SSH 窗口绑定独立 WebShellContext；
5. 将 SSH 远程状态、日志和连接操作放入对应母窗口。

## 阶段五：远程目录选择器 Web 化

1. 将添加工作区拦截保留在对应 SSH WebView；
2. 将 RemoteFolderBrowserForm 替换为 Web 目录选择器；
3. 通过 WebMessage 调用 SshConnection 远程目录查询；
4. 支持路径导航、返回上级、刷新、目录选择和取消；
5. 验证多 SSH 窗口并发操作不会串目录或串连接；
6. 通过现有工作区添加逻辑提交选择结果。

## 阶段六：清理与回归

1. 移除旧窗口的用户入口；
2. 评估并删除不再使用的 LogForm、PluginsForm、SettingsForm、ConnectionPickerForm、SshEditForm、RemoteFolderBrowserForm；
3. 更新快捷键、托盘菜单、README 和使用说明；
4. 增加 Web Shell、窗口控制、主题、Modal、SSH 目录和多窗口测试；
5. 完成 launcher 构建和 Windows WebView2 手工验证。

## 交付验收

- MainForm 与 ConnectionWindow 均无系统标题栏；
- 标题栏颜色跟随 dsh 主题变化；
- 设置、日志、插件、关于、SSH 连接均为 Web 弹窗；
- SSH 添加工作区弹窗显示在对应母窗口；
- 现有 dsh WebSocket、Cookie、Manager Agent、HTTP 代理、快捷键和托盘功能正常；
- 本地与远程连接之间没有状态串扰。

### 阶段三至六进度（已实现）

- 日志、插件、SSH 连接选择、SSH 编辑及远程目录入口已切换到当前 WebView2 的独立 Web Modal context；旧 WinForms 类保留作为回退代码。
- ConnectionWindow 的远程目录拦截继续由所属 SSH WebView 处理，避免多窗口串联。
- WebModalRouter 增加通用 modal 页面路由及独立窗口上下文入口。
