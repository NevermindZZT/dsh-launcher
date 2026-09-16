using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

internal static class WebModalRouter
{
    public static void Open(WebView2 web, AppSettings s, string page)
    {
        var data = new
        {
            page,
            version = VersionHelper.Current,
            attachPort = s.AttachPort,
            workingDirectory = s.WorkingDirectory,
            manager = new { s.Manager.Enabled, s.Manager.ServerUrl, s.Manager.AgentName, s.Manager.PairingCode },
            closeExits = s.CloseExits,
            autoStart = s.AutoStart,
            openLinksInWebView = s.OpenLinksInWebView,
            handleAgentQuestions = s.HandleAgentQuestions,
            ssh = s.SshConnections.Select(x => new { x.Name, x.Host, x.Port, x.User, x.AuthMethod, x.KeyPath, x.LocalPort, x.RemotePort, x.AutoConnect }),
            history = "",
            plugins = Array.Empty<object>(),
        };
        Open(web, page, data);
    }

    public static void Open(WebView2 web, string page, object? data = null)
    {
        var cwv = web.CoreWebView2;
        if (cwv == null)
        {
            Diag.Log($"WebModalRouter.Open skipped page={page}: CoreWebView2 is null");
            return;
        }
        var json = JsonSerializer.Serialize(data ?? new { page });
        var payload = JsonSerializer.Serialize(json);
        var script = "(function(){try{document.getElementById('dsh-modal')?.remove();if(!window.__dshModal){" + Script + "}window.__dshModal(" + payload + ");}catch(e){document.body.setAttribute('data-dsh-modal-error',String(e));throw e;}})();";
        _ = cwv.ExecuteScriptAsync(script);
    }

    public static void Close(WebView2 web)
    {
        if (web.CoreWebView2 == null) return;
        _ = web.CoreWebView2.ExecuteScriptAsync("document.getElementById('dsh-modal')?.remove();");
    }

    public static async Task Install(WebView2 web)
    {
        if (web.CoreWebView2 != null)
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(Script);
    }

    public static bool TryHandle(string raw, Action<string, JsonElement> action)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!string.Equals(Prop(root, "type").GetString(), "modal", StringComparison.OrdinalIgnoreCase)) return false;
            action(Prop(root, "action").GetString() ?? "", root.TryGetProperty("payload", out var payload) ? payload : default);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(AppSettings s, JsonElement data)
    {
        var port = Prop(data, "attachPort");
        if (port.TryGetInt32(out var attachPort)) s.AttachPort = attachPort;
        var workingDirectory = Prop(data, "workingDirectory");
        if (workingDirectory.ValueKind == JsonValueKind.String) s.WorkingDirectory = workingDirectory.GetString();

        var closeExits = Prop(data, "closeExits");
        if (closeExits.ValueKind is JsonValueKind.True or JsonValueKind.False) s.CloseExits = closeExits.GetBoolean();
        var autoStart = Prop(data, "autoStart");
        if (autoStart.ValueKind is JsonValueKind.True or JsonValueKind.False) s.AutoStart = autoStart.GetBoolean();
        var links = Prop(data, "openLinksInWebView");
        if (links.ValueKind is JsonValueKind.True or JsonValueKind.False) s.OpenLinksInWebView = links.GetBoolean();
        var questions = Prop(data, "handleAgentQuestions");
        if (questions.ValueKind is JsonValueKind.True or JsonValueKind.False) s.HandleAgentQuestions = questions.GetBoolean();

        var oldManagerUrl = s.Manager.ServerUrl;
        var manager = Prop(data, "manager");
        if (manager.ValueKind == JsonValueKind.Object)
        {
            var enabled = Prop(manager, "enabled");
            if (enabled.ValueKind is JsonValueKind.True or JsonValueKind.False) s.Manager.Enabled = enabled.GetBoolean();
            s.Manager.ServerUrl = Str(manager, "serverUrl");
            s.Manager.AgentName = Str(manager, "agentName");
            s.Manager.PairingCode = Str(manager, "pairingCode");
        }
        if (!string.Equals(oldManagerUrl, s.Manager.ServerUrl, StringComparison.OrdinalIgnoreCase))
        {
            s.Manager.AgentId = "";
            s.Manager.AgentToken = "";
        }
        s.Save();
        s.ApplyAutoStart();
    }

    private static string Str(JsonElement value, string name)
    {
        var property = Prop(value, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    }

    private static JsonElement Prop(JsonElement value, string name)
    {
        foreach (var property in value.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private const string Script = """
(function(){
  if(window.__dshModal)return;
  var zh=(navigator.language||'').toLowerCase().indexOf('zh')===0;
  var t=function(c,e){return zh?c:e};
  var send=function(a,p){chrome.webview.postMessage(JSON.stringify({type:'modal',action:a,payload:p||{}}));};
  var esc=function(x){return String(x??'').replace(/[&<>"']/g,function(c){return {"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]})};

  window.__dshModal=function(raw){
     var parsed=JSON.parse(raw);if(typeof parsed==='string'){try{parsed=JSON.parse(parsed)}catch(_){}}var m=parsed||{},page=String(m.page||'').trim().toLowerCase(),titles={settings:t('设置','Settings'),manager:t('dsh-manager','dsh-manager'),about:t('关于','About'),logs:t('运行日志','Logs'),plugins:t('插件管理','Plugins'),ssh:t('SSH 连接','SSH Connections'),'ssh-edit':t('SSH 连接配置','SSH Connection')},body='';if(!titles[page])page='about';
    window.__dshSelectedPlugin='';

    if(page==='manager'){
      var mm=m.manager||{},agents=mm.agents||[],instances=mm.instances||[],proxy=(mm.diagnostics&&mm.diagnostics.proxy)||{};
      var managerError=mm.error?'<p class="manager-error">'+esc(mm.error)+'</p>':'';
      var managerNotice=mm.notice?'<p class="manager-notice">'+esc(mm.notice)+'</p>':'';
      if(!mm.authenticated){
        body='<fieldset><legend>'+t('连接 dsh-manager','Connect to dsh-manager')+'</legend>'+managerError+
          '<div class="form-row"><label>'+t('服务器地址','Server URL')+'</label><input id="manager-url" value="'+esc(mm.serverUrl||'')+'" placeholder="https://manager.example.com"></div>'+
          '<div class="form-row"><label>'+t('用户名','Username')+'</label><input id="manager-username" value="'+esc(mm.username||'')+'" autocomplete="username"></div>'+
          '<div class="form-row"><label>'+t('密码','Password')+'</label><input id="manager-password" type="password" autocomplete="current-password"></div>'+
          '<div class="actions"><button class="primary" id="manager-login">'+t('登录','Sign in')+'</button></div></fieldset>';
      }else{
        body='<div class="toolbar"><b>'+t('已连接','Connected')+'</b><span class="manager-muted">'+esc(mm.username||'')+(mm.managerVersion?' · '+esc(mm.managerVersion):'')+'</span><button id="manager-refresh">'+t('刷新','Refresh')+'</button><button id="manager-logout">'+t('退出登录','Sign out')+'</button></div>'+managerNotice+managerError+
          '<section class="manager-section"><h3>'+t('Agents','Agents')+' <small>'+agents.length+'</small></h3><div class="manager-list">'+
          (agents.length?agents.map(function(x){return '<article class="manager-card"><div class="manager-card-head"><b>'+esc(x.name||x.agentId)+'</b><span class="manager-pill '+(x.online?'online':'offline')+'">'+(x.online?t('在线','Online'):t('离线','Offline'))+'</span></div><div class="manager-muted">'+esc(x.platform||'')+' · '+esc(x.agentVersion||x.launcherVersion||'')+'</div><code>'+esc(x.agentId)+'</code></article>';}).join(''):('<p class="manager-muted">'+t('暂无 Agent','No agents')+'</p>'))+'</div></section>'+
          '<section class="manager-section"><h3>'+t('dsh 实例','dsh instances')+' <small>'+instances.length+'</small></h3><div class="manager-list">'+
          (instances.length?instances.map(function(x){var ready=!!x.urlAvailable&&String(x.state||'').toLowerCase()==='running';return '<article class="manager-card"><div class="manager-card-head"><div><b>'+esc(x.displayName||x.instanceId)+'</b><div class="manager-muted">'+esc(x.agentName||x.agentId)+' · '+esc(x.instanceId)+'</div></div><span class="manager-pill '+esc(String(x.state||'').toLowerCase())+'">'+esc(x.state||'unknown')+'</span></div><div class="manager-muted">'+esc(x.version||'')+(x.error?' · '+esc(x.error):'')+'</div><div class="manager-actions"><button data-manager-action="start" data-agent="'+esc(x.agentId)+'" data-instance="'+esc(x.instanceId)+'">'+t('启动','Start')+'</button><button data-manager-action="stop" data-agent="'+esc(x.agentId)+'" data-instance="'+esc(x.instanceId)+'">'+t('停止','Stop')+'</button><button data-manager-action="restart" data-agent="'+esc(x.agentId)+'" data-instance="'+esc(x.instanceId)+'">'+t('重启','Restart')+'</button><button data-manager-action="sync" data-agent="'+esc(x.agentId)+'" data-instance="'+esc(x.instanceId)+'">'+t('同步','Sync')+'</button><button data-manager-action="update" data-agent="'+esc(x.agentId)+'" data-instance="'+esc(x.instanceId)+'">'+t('更新','Update')+'</button>'+(ready?'<button class="primary" data-manager-open="1" data-agent="'+esc(x.agentId)+'" data-instance="'+esc(x.instanceId)+'">'+t('打开 dsh','Open dsh')+'</button>':'')+'</div></article>';}).join(''):('<p class="manager-muted">'+t('暂无实例','No instances')+'</p>'))+'</div></section>'+
          '<section class="manager-section manager-diagnostics"><h3>'+t('代理诊断','Proxy diagnostics')+'</h3><div class="manager-muted">'+t('WebSocket 打开失败','WS open failures')+': '+esc(proxy.wsOpenFailed||0)+' · '+t('心跳失败','Heartbeat failures')+': '+esc(proxy.wsHeartbeatFailed||0)+' · '+t('丢弃 tunnel','Dropped tunnels')+': '+esc(proxy.tunnelDropped||0)+'</div></section>';
      }
    }else if(page==='settings'){
      body='<fieldset><legend>'+t('本地 dsh','Local dsh')+'</legend>'+
        '<div class="form-row"><label>'+t('端口','Port')+'</label><input id="port" value="'+esc(m.attachPort||0)+'"></div>'+
        '<div class="form-row"><label>'+t('工作目录','Working directory')+'</label><input id="wd" value="'+esc(m.workingDirectory||'')+'"></div></fieldset>'+
        '<fieldset><legend>dsh-manager</legend>'+
        '<div class="form-row"><label>'+t('启用','Enabled')+'</label><input id="men" type="checkbox" '+((m.manager?.Enabled||m.manager?.enabled)?'checked':'')+'></div>'+
        '<div class="form-row"><label>'+t('服务器地址','Server URL')+'</label><input id="url" value="'+esc((m.manager?.ServerUrl||m.manager?.serverUrl)||'')+'"></div>'+
        '<div class="form-row"><label>'+t('Agent 名称','Agent name')+'</label><input id="an" value="'+esc((m.manager?.AgentName||m.manager?.agentName)||'')+'"></div>'+
        '<div class="form-row"><label>'+t('首次配对码（仅注册时使用）','Initial pairing code (enrollment only)')+'</label><input id="pc" value="'+esc((m.manager?.PairingCode||m.manager?.pairingCode)||'')+'"></div></fieldset>'+
        '<fieldset><legend>'+t('启动与关闭','Startup & Shutdown')+'</legend>'+
        '<div class="form-row"><label>'+t('关闭时退出','Close exits')+'</label><input id="ce" type="checkbox" '+(m.closeExits?'checked':'')+'></div>'+
        '<div class="form-row"><label>'+t('自动启动','Auto-start')+'</label><input id="as" type="checkbox" '+(m.autoStart?'checked':'')+'></div></fieldset>'+
        '<fieldset><legend>'+t('Agent 交互','Agent interactions')+'</legend>'+
        '<div class="form-row"><label>'+t('同时显示 Launcher 的提问和权限浮窗','Also show Launcher question and approval pop-ups')+'</label><input id="aq" type="checkbox" '+(m.handleAgentQuestions!==false?'checked':'')+'></div>'+
        '<div class="manager-muted">'+t('dsh 原生 Web UI 始终显示；关闭后仅关闭 Launcher 浮窗和通知。','dsh native Web UI always remains available; disabling only hides Launcher pop-ups and notifications.')+'</div></fieldset>'+
        '<fieldset><legend>'+t('链接打开方式','Link opening')+'</legend>'+
        '<div class="form-row"><label>'+t('链接使用 WebView2','Open links in WebView2')+'</label><input id="lw" type="checkbox" '+(m.openLinksInWebView?'checked':'')+'></div></fieldset>'+
        '<div class="actions"><button class="primary" id="save">'+t('保存设置','Save')+'</button></div>';
    }else if(page==='plugins'){
      var canManage=m.canManagePlugins!==false;
      var toolbar='<div class="toolbar"><input id="pkg" placeholder="'+t('包名','Package')+'"><button class="primary" id="install">'+t('添加','Install')+'</button><button id="remove">'+t('移除选中','Remove selected')+'</button><button id="update">'+t('更新选中','Update selected')+'</button><button id="list">'+t('刷新','Refresh')+'</button>';
      if(canManage)toolbar+='<button id="plugins-export">'+t('导出列表','Export list')+'</button><button id="plugins-import">'+t('导入列表','Import list')+'</button>';
      toolbar+='</div>';
      body=toolbar+'<div class="plugin-grid" id="plist">'+(m.plugins||[]).map(function(x){
        var p=x.package||x.Package||'',spec=x.spec||x.Spec||'',version=x.version||x.Version||'',kind=(x.isBundle||x.IsBundle)?'bundle':((x.isTemplate||x.IsTemplate)?'template':'plugin');
        var shown=version||spec||t('未指定版本','unspecified');
        var declared=version&&spec&&version!==spec?' · '+t('声明','spec')+': '+spec:'';
        return '<article class="plugin-row" data-pkg="'+esc(p)+'"><button class="plugin-select" type="button"><b>'+esc(p)+'</b><small>'+esc(kind)+' · '+esc(shown)+esc(declared)+'</small></button></article>';
      }).join('')+'</div>';
    }else if(page==='logs'){
      body='<textarea id="logbox" readonly>'+esc(m.history||'')+'</textarea><div class="actions"><button id="clear">'+t('清空','Clear')+'</button></div>';
    }else if(page==='ssh'){
      body='<div class="actions"><button class="primary" id="add">'+t('新增 SSH','Add SSH')+'</button></div><div class="items">'+(m.ssh||[]).map(function(x){
        var n=x.name||x.Name;
        return '<div class="item"><div><b>'+esc(n)+'</b><span>'+esc((x.user||x.User)+'@'+(x.host||x.Host))+':'+(x.port||x.Port)+'</span></div><div><button data-edit="'+esc(n)+'">'+t('编辑','Edit')+'</button><button data-del="'+esc(n)+'">'+t('删除','Delete')+'</button><button class="primary" data-connect="'+esc(n)+'">'+t('连接','Connect')+'</button></div></div>';
      }).join('')+'</div>';
    }else if(page==='ssh-edit'){
      var c=m.config||{};
      body='<div class="form-row"><label>'+t('名称','Name')+'</label><input id="ssh-name" value="'+esc(c.name||c.Name||'')+'"></div>'+
        '<div class="form-row"><label>'+t('主机','Host')+'</label><input id="ssh-host" value="'+esc(c.host||c.Host||'')+'"></div>'+
        '<div class="form-row"><label>'+t('端口','Port')+'</label><input id="ssh-port" type="number" value="'+esc(c.port||c.Port||22)+'"></div>'+
        '<div class="form-row"><label>'+t('用户名','User')+'</label><input id="ssh-user" value="'+esc(c.user||c.User||'')+'"></div>'+
        '<div class="form-row"><label>'+t('认证方式','Authentication')+'</label><select id="ssh-auth"><option value="key" '+((c.authMethod||c.AuthMethod||'key')==='key'?'selected':'')+'>'+t('私钥','Private key')+'</option><option value="password" '+((c.authMethod||c.AuthMethod)==='password'?'selected':'')+'>'+t('密码','Password')+'</option></select></div>'+
        '<div class="form-row"><label>'+t('私钥路径','Private key path')+'</label><input id="ssh-key" value="'+esc(c.keyPath||c.KeyPath||'')+'"></div>'+
        '<div class="form-row"><label>'+t('密码','Password')+'</label><input id="ssh-password" type="password" value="'+esc(c.password||c.Password||'')+'"></div>'+
        '<div class="form-row"><label>'+t('本地转发端口','Local port')+'</label><input id="ssh-local" type="number" value="'+esc(c.localPort||c.LocalPort||0)+'"></div>'+
        '<div class="form-row"><label>'+t('远端 dsh 端口','Remote dsh port')+'</label><input id="ssh-remote" type="number" value="'+esc(c.remotePort||c.RemotePort||0)+'"></div>'+
        '<div class="form-row"><label>'+t('远端 Node 路径','Remote Node')+'</label><input id="ssh-node" value="'+esc(c.remoteNode||c.RemoteNode||'')+'"></div>'+
        '<div class="form-row"><label>'+t('远端 dsh 路径','Remote dsh path')+'</label><input id="ssh-dsh" value="'+esc(c.remoteDshBin||c.RemoteDshBin||'')+'"></div>'+
        '<div class="form-row"><label>'+t('关闭时停止远端','Stop remote on close')+'</label><input id="ssh-stop" type="checkbox" '+((c.stopRemoteOnClose??c.StopRemoteOnClose??true)?'checked':'')+'></div>'+
        '<div class="form-row"><label>'+t('启动时自动连接','Auto-connect')+'</label><input id="ssh-auto" type="checkbox" '+((c.autoConnect??c.AutoConnect??true)?'checked':'')+'></div>'+
        '<div class="actions"><button class="primary" id="ssh-save">'+t('保存','Save')+'</button><button id="ssh-cancel">'+t('取消','Cancel')+'</button></div>';
    }else if(page==='about'){
      var dshState=m.dshVersionState||((m.dshVersion)?'ready':'missing');
      var dshText=dshState==='loading'?t('读取中…','Loading…'):(m.dshVersion||t('未检测到','Not detected'));
      var launcherState=m.launcherUpdateState||'idle';
      var dshUpdateState=m.dshUpdateState||'idle';
      var checking=launcherState==='checking'||dshUpdateState==='checking';
      var checkLabel=checking?t('检查中…','Checking…'):t('检查更新','Check for updates');
      var launcherText=m.launcherUpdateMessage||(launcherState==='checking'?t('检查中…','Checking…'):t('尚未检查','Not checked yet'));
      var dshUpdateText=m.dshUpdateMessage||(dshUpdateState==='checking'?t('检查中…','Checking…'):t('尚未检查','Not checked yet'));
      var launcherClass=launcherState==='error'?' error':(launcherState==='available'?' available':'');
      var dshClass=dshUpdateState==='error'?' error':(dshUpdateState==='available'?' available':'');
      var launcherActions='';
      if(launcherState==='available'&&m.launcherDownloadUrl)launcherActions+='<a class="about-link" href="'+esc(m.launcherDownloadUrl)+'">'+t('下载新版本','Download')+'</a>';
      if(launcherState==='available'&&m.launcherReleaseUrl)launcherActions+='<a class="about-link" href="'+esc(m.launcherReleaseUrl)+'">'+t('发行说明','Release notes')+'</a>';
      var dshAction=(m.canUpdateDsh!==false&&dshUpdateState==='available')?'<button id="dsh-update">'+t('更新 dsh','Update dsh')+'</button>':'';
      body='<div class="about">'+
        '<strong>DshLauncher</strong>'+
        '<div class="about-row"><span>'+t('启动器版本','Launcher version')+'</span><b>'+esc(m.version||'')+'</b></div>'+
        '<div class="about-row"><span>dsh '+t('实例版本','instance version')+'</span><b>'+esc(dshText)+'</b></div>'+
        '<a class="about-link" href="'+esc(m.projectUrl||'')+'">'+esc(m.projectUrl||t('DshLauncher 项目主页','DshLauncher project'))+'</a>'+
        '<div class="about-update"><button id="launcher-check-update"'+(checking?' disabled':'')+'>'+checkLabel+'</button></div>'+
        '<div class="about-update-list">'+
          '<div class="about-update-row"><b>DshLauncher</b><span class="about-status'+launcherClass+'">'+esc(launcherText)+'</span><span class="about-update-actions">'+launcherActions+'</span></div>'+
          '<div class="about-update-row"><b>dsh</b><span class="about-status'+dshClass+'">'+esc(dshUpdateText)+'</span><span class="about-update-actions">'+dshAction+'</span></div>'+
        '</div>'+
        '</div>';
    }else{
      body='<div class="about"><strong>DshLauncher</strong></div>';
    }

    var o=document.createElement('div');
    o.id='dsh-modal';
    var standalone=window.__dshLauncherStandaloneModal===true;
    o.innerHTML=standalone
      ? '<section class="card standalone" role="main"><main class="modal-main">'+body+'</main></section>'
      : '<div class="backdrop"></div><section class="card" role="dialog" aria-modal="true" aria-labelledby="dsh-modal-title"><header class="modal-header"><div><p class="eyebrow">DshLauncher</p><h2 id="dsh-modal-title">'+(titles[page]||t('关于','About'))+'</h2></div><button class="close" id="x" aria-label="'+t('关闭','Close')+'">×</button></header><main class="modal-main">'+body+'</main><footer class="modal-footer"><button id="cancel">'+t('关闭','Close')+'</button></footer></section>';
    document.body.appendChild(o);
    var st=document.createElement('style');
    st.textContent='#dsh-modal{--dsh-modal-border:color-mix(in srgb,var(--dsh-launcher-fg,#111827) 22%,transparent);--dsh-modal-border-strong:color-mix(in srgb,var(--dsh-launcher-fg,#111827) 28%,transparent);--dsh-modal-border-input:color-mix(in srgb,var(--dsh-launcher-fg,#111827) 32%,transparent);--dsh-modal-border-button:color-mix(in srgb,var(--dsh-launcher-fg,#111827) 26%,transparent);--dsh-modal-border-row:color-mix(in srgb,var(--dsh-launcher-fg,#111827) 24%,transparent);position:fixed;inset:0;z-index:2147483647;display:grid;place-items:center;font:14px/1.45 Segoe UI,sans-serif;color:var(--dsh-launcher-fg)}#dsh-modal *{box-sizing:border-box}#dsh-modal .backdrop{position:absolute;inset:0;background:rgba(0,0,0,.58)}#dsh-modal .card{position:relative;width:min(680px,calc(100vw - 32px));max-height:min(88vh,760px);overflow:auto;background:var(--dsh-launcher-bg);border:1px solid var(--dsh-modal-border-strong);border-radius:12px;box-shadow:0 24px 80px rgba(0,0,0,.42);padding:0}#dsh-modal .modal-header{display:flex;align-items:flex-start;justify-content:space-between;padding:18px 20px 14px;border-bottom:1px solid var(--dsh-modal-border)}#dsh-modal .eyebrow{margin:0 0 2px;color:var(--dsh-launcher-accent);font-size:11px;font-weight:600;letter-spacing:.08em;text-transform:uppercase}#dsh-modal h2{margin:0;font-size:20px;font-weight:650}#dsh-modal .close{margin:0;padding:2px 8px;border:0;background:transparent;color:inherit;font-size:24px;line-height:1;cursor:pointer}#dsh-modal .close:hover{color:var(--dsh-launcher-accent)}#dsh-modal .modal-main{padding:16px 20px}#dsh-modal fieldset{margin:0 0 12px;padding:12px 14px;border:1px solid var(--dsh-modal-border-strong);border-radius:9px}#dsh-modal legend{padding:0 6px;color:var(--dsh-launcher-accent);font-weight:600}#dsh-modal .form-row{display:grid;grid-template-columns:minmax(150px,190px) minmax(0,1fr);align-items:center;gap:12px;margin:9px 0}#dsh-modal input,#dsh-modal textarea,#dsh-modal select{width:100%;border:1px solid var(--dsh-modal-border-input);border-radius:6px;background:rgba(0,0,0,.16);color:inherit;padding:8px 9px}#dsh-modal select option{background:var(--dsh-launcher-bg,#1f1f1f);color:var(--dsh-launcher-fg,#f4f4f5)}#dsh-modal input[type=checkbox]{width:auto;accent-color:var(--dsh-launcher-accent)}#dsh-modal button{border:1px solid var(--dsh-modal-border-button);border-radius:6px;background:rgba(255,255,255,.07);color:inherit;padding:7px 11px;cursor:pointer}#dsh-modal button:hover{border-color:var(--dsh-launcher-accent);background:rgba(112,165,255,.14)}#dsh-modal button:disabled{opacity:.65;cursor:wait}#dsh-modal button.primary{border-color:var(--dsh-launcher-accent);background:var(--dsh-launcher-accent);color:#111827}#dsh-modal .actions,#dsh-modal .toolbar{display:flex;flex-wrap:wrap;gap:6px;align-items:center}#dsh-modal .toolbar{margin-bottom:12px}#dsh-modal .plugin-row,#dsh-modal .item{margin:8px 0;padding:10px;border:1px solid var(--dsh-modal-border-row);border-radius:8px}#dsh-modal .plugin-row.selected{border-color:var(--dsh-launcher-accent);background:rgba(112,165,255,.14)}#dsh-modal .plugin-select{display:flex;width:100%;justify-content:space-between;text-align:left;border:0;background:transparent}#dsh-modal .plugin-select small,#dsh-modal .item span{display:block;color:color-mix(in srgb,var(--dsh-launcher-fg,#111827) 68%,transparent)}#dsh-modal .item{display:flex;justify-content:space-between;gap:12px;align-items:center}#dsh-modal .item>div:last-child{display:flex;align-items:center;justify-content:flex-end;gap:8px;flex-wrap:wrap}#dsh-modal #logbox{min-height:280px;resize:vertical;font:12px/1.5 Consolas,monospace}#dsh-modal .about{display:flex;flex-direction:column;gap:12px;padding:4px 0 8px}#dsh-modal .about>strong{font-size:22px}#dsh-modal .about-row{display:flex;justify-content:space-between;gap:20px;padding:9px 0;border-bottom:1px solid var(--dsh-modal-border)}#dsh-modal .about-row span{color:color-mix(in srgb,var(--dsh-launcher-fg,#111827) 72%,transparent)}#dsh-modal .about-row b{overflow-wrap:anywhere;text-align:right}#dsh-modal .about-link{color:var(--dsh-launcher-accent);overflow-wrap:anywhere;text-decoration:none}#dsh-modal .about-link:hover{text-decoration:underline}#dsh-modal .about-update{display:flex;align-items:center;gap:10px;flex-wrap:wrap}#dsh-modal .about-update-list{display:flex;flex-direction:column;gap:8px}#dsh-modal .about-update-row{display:grid;grid-template-columns:minmax(92px,120px) minmax(0,1fr) auto;align-items:center;gap:10px;padding:9px 0;border-bottom:1px solid var(--dsh-modal-border)}#dsh-modal .about-update-row>b{font-weight:650}#dsh-modal .about-update-actions{display:flex;align-items:center;gap:8px;flex-wrap:wrap}#dsh-modal .about-status{color:color-mix(in srgb,var(--dsh-launcher-fg,#111827) 72%,transparent);overflow-wrap:anywhere}#dsh-modal .about-status.available{color:var(--dsh-launcher-accent);font-weight:600}#dsh-modal .about-status.error{color:#f97373}#dsh-modal pre{max-height:220px;overflow:auto;padding:10px;border-radius:7px;background:rgba(0,0,0,.18)}#dsh-modal .folder-list{max-height:220px;overflow:auto;display:flex;flex-direction:column;gap:4px;padding:8px;border-radius:7px;background:rgba(0,0,0,.18)}#dsh-modal .folder-list button{display:flex;align-items:center;gap:8px;width:100%;padding:6px 8px;border:0;border-radius:5px;background:transparent;text-align:left;font:13px/1.35 Consolas,monospace;color:inherit;cursor:pointer}#dsh-modal .folder-list button:hover{background:rgba(112,165,255,.16);color:var(--dsh-launcher-accent)}#dsh-modal .folder-file{display:flex;align-items:center;gap:8px;padding:6px 8px;font:13px/1.35 Consolas,monospace;opacity:.7}#dsh-modal .folder-icon{display:inline-block;width:20px;text-align:center;font-family:Segoe UI Emoji,Segoe UI Symbol,sans-serif}#dsh-modal .modal-footer{display:flex;justify-content:flex-end;padding:12px 20px 16px;border-top:1px solid var(--dsh-modal-border)}#dsh-modal a{cursor:pointer}#dsh-modal .manager-error{margin:0 0 12px;color:#f97373;overflow-wrap:anywhere}#dsh-modal .manager-notice{margin:0 0 12px;color:var(--dsh-launcher-accent);overflow-wrap:anywhere}#dsh-modal .manager-section{margin:0 0 16px}#dsh-modal .manager-section h3{display:flex;align-items:center;gap:8px;margin:0 0 8px;font-size:14px}#dsh-modal .manager-section h3 small{font-size:12px;font-weight:400;opacity:.72}#dsh-modal .manager-list{display:flex;flex-direction:column;gap:8px}#dsh-modal .manager-card{padding:10px 12px;border:1px solid var(--dsh-modal-border-row);border-radius:9px}#dsh-modal .manager-card-head{display:flex;align-items:flex-start;justify-content:space-between;gap:10px}#dsh-modal .manager-muted{color:color-mix(in srgb,var(--dsh-launcher-fg,#111827) 68%,transparent);overflow-wrap:anywhere}#dsh-modal .manager-card code{display:block;margin-top:5px;font-size:11px;opacity:.72;overflow-wrap:anywhere}#dsh-modal .manager-pill{display:inline-flex;align-items:center;border-radius:999px;padding:2px 7px;font-size:11px;white-space:nowrap;background:rgba(255,255,255,.1)}#dsh-modal .manager-pill.online,#dsh-modal .manager-pill.running{color:#5ee59a;background:rgba(46,160,90,.18)}#dsh-modal .manager-pill.offline,#dsh-modal .manager-pill.failed{color:#f97373;background:rgba(210,70,70,.18)}#dsh-modal .manager-actions{display:flex;flex-wrap:wrap;gap:6px;margin-top:10px}@media(max-width:560px){#dsh-modal .card{width:calc(100vw - 20px);max-height:92vh}#dsh-modal .modal-main{padding:12px}#dsh-modal .form-row{grid-template-columns:1fr;gap:5px}#dsh-modal .item{align-items:stretch;flex-direction:column}#dsh-modal .about-row{align-items:flex-start;flex-direction:column;gap:4px}#dsh-modal .about-row b{text-align:left}#dsh-modal .about-update-row{grid-template-columns:1fr;align-items:start}#dsh-modal .about-update-actions{justify-content:flex-start}}';
    o.appendChild(st);
    if(standalone){
      var logbox=o.querySelector('#logbox');
      if(logbox)logbox.style.height=Math.max(360,window.innerHeight-150)+'px';
      setTimeout(function(){try{chrome.webview.postMessage(JSON.stringify({type:'modal-size',width:Math.ceil(o.scrollWidth)+24,height:Math.ceil(o.scrollHeight)+24}));}catch(_){ }},0);
    }
    try{var nums=(getComputedStyle(o).color.match(/\d+(?:\.\d+)?/g)||[]);if(nums.length>=3){var y=(+nums[0]*299+ +nums[1]*587+ +nums[2]*114)/1000;o.style.colorScheme=y>160?'dark':'light';}}catch(_){ }
    var val=function(id){return o.querySelector('#'+id)?.value||''};
    var close=function(){o.remove();};
    o.onclick=function(e){
      var el=e.target;
      if(!el||!el.closest)return;
      var row=el.closest('.plugin-row');
      if(el.id==='x'||el.id==='cancel'||el.id==='ssh-cancel'||el.classList.contains('backdrop'))close();
      else if(el.id==='manager-login'){if(el.disabled)return;el.disabled=true;el.textContent=t('登录中…','Signing in…');send('manager.login',{serverUrl:val('manager-url'),username:val('manager-username'),password:val('manager-password')});}
      else if(el.id==='manager-refresh')send('manager.refresh');
      else if(el.id==='manager-logout')send('manager.logout');
      else if(el.closest('[data-manager-action]')){var b=el.closest('[data-manager-action]');send('manager.command',{agentId:b.dataset.agent,instanceId:b.dataset.instance,action:b.dataset.managerAction});}
      else if(el.closest('[data-manager-open]')){var b=el.closest('[data-manager-open]');send('manager.open',{agentId:b.dataset.agent,instanceId:b.dataset.instance});}
      else if(el.id==='save')send('settings.save',{attachPort:+val('port'),workingDirectory:val('wd'),closeExits:o.querySelector('#ce').checked,autoStart:o.querySelector('#as').checked,openLinksInWebView:o.querySelector('#lw').checked,handleAgentQuestions:o.querySelector('#aq').checked,manager:{enabled:o.querySelector('#men').checked,serverUrl:val('url'),agentName:val('an'),pairingCode:val('pc')}});
      else if(row){window.__dshSelectedPlugin=row.dataset.pkg;o.querySelectorAll('.plugin-row').forEach(function(x){x.classList.toggle('selected',x===row)});}
      else if(el.id==='install')send('plugins.install',{package:val('pkg')});
      else if(el.id==='remove')send('plugins.remove',{package:window.__dshSelectedPlugin||val('pkg')});
      else if(el.id==='update')send('plugins.update',{package:window.__dshSelectedPlugin||val('pkg')});
      else if(el.id==='list')send('plugins.list');
      else if(el.id==='plugins-export')send('plugins.export');
      else if(el.id==='plugins-import')send('plugins.import');
      else if(el.id==='launcher-check-update')send('launcher.checkUpdate');
      else if(el.id==='dsh-update')send('dsh.update');
      else if(el.id==='clear')send('logs.clear');
      else if(el.id==='ssh-save')send('ssh.save',{originalName:m.originalName||'',name:val('ssh-name'),host:val('ssh-host'),port:+(val('ssh-port')||22),user:val('ssh-user'),authMethod:o.querySelector('#ssh-auth').value,keyPath:val('ssh-key'),password:val('ssh-password'),localPort:+(val('ssh-local')||0),remotePort:+(val('ssh-remote')||0),remoteNode:val('ssh-node'),remoteDshBin:val('ssh-dsh'),stopRemoteOnClose:o.querySelector('#ssh-stop').checked,autoConnect:o.querySelector('#ssh-auto').checked});
      else if(el.id==='add')send('ssh.form',{mode:'add'});
      else if(el.dataset.edit)send('ssh.form',{mode:'edit',name:el.dataset.edit});
      else if(el.dataset.del)send('ssh.delete',{name:el.dataset.del});
      else if(el.dataset.connect)send('ssh.connect',{name:el.dataset.connect});
    };
    o.addEventListener('keydown',function(e){if(e.key==='Escape')close();});
  };
})();
""";
}
