using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

internal static class WebModalRouter
{
    public static void Open(WebView2 web, AppSettings settings, string page)
    {
        if (web.CoreWebView2 == null) return;
        var model = new { page, general = new { attachPort = settings.AttachPort, workingDirectory = settings.WorkingDirectory ?? "", closeExits = settings.CloseExits, autoStart = settings.AutoStart, dshHome = Environment.GetEnvironmentVariable("DSH_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh") }, manager = new { enabled=settings.Manager.Enabled, serverUrl=settings.Manager.ServerUrl, agentName=settings.Manager.AgentName, pairingCode=settings.Manager.PairingCode, fingerprint=settings.Manager.ServerCertificateFingerprint, status=string.IsNullOrWhiteSpace(settings.Manager.AgentId) ? "尚未配对" : "已配对：" + settings.Manager.AgentId }, ssh = settings.SshConnections.Select(x => new { name=x.Name, host=x.Host, port=x.Port, user=x.User }).ToArray(), version=VersionHelper.Current };
        var json = JsonSerializer.Serialize(model);
        web.CoreWebView2.ExecuteScriptAsync("window.__dshModal && window.__dshModal(" + JsonSerializer.Serialize(json) + ");");
    }

    public static async Task Install(WebView2 web)
    {
        if (web.CoreWebView2 == null) return;
        await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(Script);
    }

    public static void Apply(AppSettings s, JsonElement d)
    {
        if (d.TryGetProperty("attachPort", out var p) && p.TryGetInt32(out var port)) s.AttachPort=port;
        s.WorkingDirectory = d.TryGetProperty("workingDirectory", out var cwd) && !string.IsNullOrWhiteSpace(cwd.GetString()) ? cwd.GetString()!.Trim() : null;
        if (d.TryGetProperty("closeExits", out var ce)) s.CloseExits=ce.GetBoolean();
        if (d.TryGetProperty("autoStart", out var au)) s.AutoStart=au.GetBoolean();
        if (d.TryGetProperty("manager", out var m)) { var oldUrl=s.Manager.ServerUrl; var oldFp=s.Manager.ServerCertificateFingerprint; if(m.TryGetProperty("enabled",out var e))s.Manager.Enabled=e.GetBoolean(); if(m.TryGetProperty("serverUrl",out var u))s.Manager.ServerUrl=u.GetString()??""; if(m.TryGetProperty("agentName",out var n))s.Manager.AgentName=n.GetString()??""; if(m.TryGetProperty("pairingCode",out var pc))s.Manager.PairingCode=pc.GetString()??""; if(m.TryGetProperty("fingerprint",out var fp))s.Manager.ServerCertificateFingerprint=fp.GetString()??""; if(!string.IsNullOrWhiteSpace(s.Manager.PairingCode)||!string.Equals(oldUrl,s.Manager.ServerUrl,StringComparison.OrdinalIgnoreCase)||!string.Equals(oldFp,s.Manager.ServerCertificateFingerprint,StringComparison.OrdinalIgnoreCase)){s.Manager.AgentId="";s.Manager.AgentToken="";} }
        s.Save(); s.ApplyAutoStart();
    }

    private const string Script = """
(function(){if(window.__dshModal)return;window.__dshModal=function(raw){var m=JSON.parse(raw),old=document.getElementById('dsh-modal');if(old)old.remove();var o=document.createElement('div');o.id='dsh-modal';o.innerHTML='<div class="dsh-modal-card"><button class="dsh-modal-x">×</button><h2>设置</h2><div class="dsh-tabs"><button data-tab="general">常规</button><button data-tab="manager">Manager Agent</button><button data-tab="ssh">SSH</button><button data-tab="about">关于</button></div><div id="dsh-modal-body"></div><div class="dsh-modal-actions"><button class="dsh-cancel">取消</button><button class="dsh-save">保存</button></div></div>';var st=document.createElement('style');st.textContent='#dsh-modal{position:fixed;inset:32px 0 0;background:#0008;z-index:2147483646;display:flex;align-items:center;justify-content:center;font:14px Segoe UI;color:var(--dsh-launcher-fg,#eee)}.dsh-modal-card{position:relative;width:min(720px,90vw);max-height:85vh;overflow:auto;padding:28px;background:var(--dsh-launcher-bg,#202225);border:1px solid #ffffff2a;border-radius:14px;box-shadow:0 20px 60px #000b}.dsh-modal-card h2{margin:0 0 18px}.dsh-tabs{display:flex;gap:6px;border-bottom:1px solid #ffffff22;margin-bottom:18px}.dsh-tabs button,.dsh-modal-actions button{border:0;border-radius:7px;padding:9px 14px;background:#ffffff12;color:inherit;cursor:pointer}.dsh-tabs button.active,.dsh-save{background:#2563eb!important;color:white}.dsh-field{display:grid;grid-template-columns:180px 1fr;gap:10px;align-items:center;margin:12px 0}.dsh-field input{padding:9px;border-radius:6px;border:1px solid #ffffff30;background:#0003;color:inherit}.dsh-modal-x{position:absolute;right:14px;top:10px;border:0;background:transparent;color:inherit;font-size:24px}.dsh-modal-actions{display:flex;justify-content:flex-end;gap:8px;margin-top:20px}.dsh-muted{opacity:.7}';document.head.appendChild(st);document.body.appendChild(o);var b=o.querySelector('#dsh-modal-body'),cur=m.page==='about'?'about':'general';function field(k,label,val,type){return '<label class="dsh-field"><span>'+label+'</span><input data-k="'+k+'" type="'+(type||'text')+'" value="'+String(val??'').replace(/&/g,'&amp;').replace(/"/g,'&quot;')+'"></label>'}function render(){o.querySelectorAll('[data-tab]').forEach(x=>x.classList.toggle('active',x.dataset.tab===cur));if(cur==='general')b.innerHTML=field('attachPort','attach 端口',m.general.attachPort,'number')+field('workingDirectory','工作目录',m.general.workingDirectory)+field('closeExits','关闭时退出',m.general.closeExits,'checkbox')+field('autoStart','开机自启',m.general.autoStart,'checkbox')+'<p class="dsh-muted">DSH_HOME: '+m.general.dshHome+'</p>';else if(cur==='manager')b.innerHTML=field('manager.enabled','启用 Agent',m.manager.enabled,'checkbox')+field('manager.serverUrl','服务器地址',m.manager.serverUrl)+field('manager.agentName','Agent 名称',m.manager.agentName)+field('manager.pairingCode','配对码',m.manager.pairingCode)+field('manager.fingerprint','TLS 指纹',m.manager.fingerprint)+'<p class="dsh-muted">'+m.manager.status+'</p>';else if(cur==='ssh')b.innerHTML=(m.ssh.length?m.ssh.map(x=>'<p><b>'+x.name+'</b> — '+x.user+'@'+x.host+':'+x.port+'</p>').join(''):'<p class="dsh-muted">暂无 SSH 连接</p>');else b.innerHTML='<p>DeepSeek Harness 启动器</p><p>Launcher '+m.version+'</p><p class="dsh-muted">dsh WebView2 宿主</p>'; }function close(){o.remove()}o.querySelectorAll('[data-tab]').forEach(x=>x.onclick=()=>{cur=x.dataset.tab;render()});o.querySelector('.dsh-modal-x').onclick=close;o.querySelector('.dsh-cancel').onclick=close;o.querySelector('.dsh-save').onclick=function(){var d={manager:{}};b.querySelectorAll('[data-k]').forEach(x=>{var k=x.dataset.k,v=x.type==='checkbox'?x.checked:x.value;if(k.indexOf('manager.')===0)d.manager[k.slice(8)]=v;else d[k]=v});window.chrome.webview.postMessage(JSON.stringify({type:'launcher',action:'modal.save',data:d}));close()};render()};})();
""";
}
