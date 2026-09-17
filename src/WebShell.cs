namespace DshLauncher;

internal static class WebShell
{
    public const string Script = """
(function(){
 var zh=(navigator.language||'').toLowerCase().indexOf('zh')===0;var t=function(c,e){return zh?c:e};
 if(window.__dshLauncherShell)return; window.__dshLauncherShell=true;
 function send(a){try{window.chrome.webview.postMessage(JSON.stringify({type:'launcher',action:a}))}catch(_){} }
 function setTitle(v){var e=document.getElementById('dsh-launcher-title');if(e)e.textContent=v||'DeepSeek Harness'}window.__dshLauncherSetTitle=setTitle;
 function notifyHost(title,opt){try{opt=opt||{};window.chrome.webview.postMessage(JSON.stringify({type:'notification',title:String(title||''),body:String(opt.body||''),tag:opt.tag||null,requireInteraction:!!opt.requireInteraction,icon:opt.icon||null}))}catch(_){} }
 function installNotifications(){if(window.__dshLauncherNotifications)return;window.__dshLauncherNotifications=true;var Native=window.Notification;if(Native){function LauncherNotification(title,opt){if(!(this instanceof LauncherNotification))throw new TypeError('Notification constructor requires new');opt=opt||{};notifyHost(title,opt);var n=new EventTarget();n.title=String(title||'');n.body=String(opt.body||'');n.tag=String(opt.tag||'');n.icon=String(opt.icon||'');n.close=function(){};setTimeout(function(){try{n.dispatchEvent(new Event('show'))}catch(_){}},0);return n}LauncherNotification.permission='granted';LauncherNotification.requestPermission=function(){return Promise.resolve('granted')};LauncherNotification.prototype=Native.prototype;try{window.Notification=LauncherNotification}catch(_){} }if(window.ServiceWorkerRegistration&&window.ServiceWorkerRegistration.prototype&&window.ServiceWorkerRegistration.prototype.showNotification){try{window.ServiceWorkerRegistration.prototype.showNotification=function(title,opt){notifyHost(title,opt||{});return Promise.resolve()}}catch(_){}} }
 function theme(){var r=getComputedStyle(document.documentElement),b=getComputedStyle(document.body),cls=((document.documentElement.className||'')+' '+(document.body.className||'')).toLowerCase(),bg=r.backgroundColor,fg=r.color||b.color,nums=(bg.match(/\d+(?:\.\d+)?/g)||[]),dark=/(^|\s|-)dark(\s|$|-)/.test(cls)||(!/(^|\s|-)light(\s|$|-)/.test(cls)&&window.matchMedia&&matchMedia('(prefers-color-scheme: dark)').matches);if(nums.length>=3){var y=(+nums[0]*299+ +nums[1]*587+ +nums[2]*114)/1000;dark=y<150}if(!bg||bg==='transparent'||bg==='rgba(0, 0, 0, 0)')bg=b.backgroundColor;if(!bg||bg==='transparent'||bg==='rgba(0, 0, 0, 0)')bg=dark?'#1f1f1f':'#ffffff';if(!fg||fg==='transparent')fg=dark?'#f4f4f5':'#242424';document.documentElement.style.setProperty('--dsh-launcher-bg',bg);document.documentElement.style.setProperty('--dsh-launcher-fg',fg);document.documentElement.style.setProperty('--dsh-launcher-accent',r.getPropertyValue('--accent').trim()||r.getPropertyValue('--color-accent').trim()||(dark?'#70a5ff':'#2563eb'))}
 /* legacy client-area shell disabled
 function install(){if(!document.body)return;if(window.__dshLauncherNativeChrome){installNotifications();theme();return}if(document.getElementById('dsh-launcher-shell'))return;var s=document.createElement('style');s.id='dsh-launcher-style';s.textContent=' #dsh-launcher-shell{position:fixed;z-index:2147483647;top:0;left:0;right:0;height:32px;display:flex;align-items:center;font:13px Segoe UI,system-ui,sans-serif;color:var(--dsh-launcher-fg,#f4f4f5);background:var(--dsh-launcher-bg,#15171a);border-bottom:1px solid #ffffff22;user-select:none;backdrop-filter:blur(18px)}#dsh-launcher-shell *{box-sizing:border-box}#dsh-launcher-drag{height:100%;display:flex;align-items:center;flex:1;min-width:0;padding:0 12px}#dsh-launcher-title{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}#dsh-launcher-menus,#dsh-launcher-window{display:flex;align-items:center;height:100%;gap:2px;padding:0 5px}#dsh-launcher-shell button{border:0;background:transparent;color:inherit;border-radius:6px;height:24px;padding:0 9px;cursor:pointer}#dsh-launcher-shell button:hover{background:#ffffff1f}#dsh-launcher-window button{width:38px;padding:0}#dsh-launcher-window button[data-action=close]:hover{background:#c42b45;color:#fff}.dsh-launcher-menu{position:absolute;top:30px;min-width:150px;padding:5px;background:var(--dsh-launcher-bg,#202225);border:1px solid #ffffff26;border-radius:8px;box-shadow:0 12px 30px #0008;display:none}.dsh-launcher-menu.open{display:block}.dsh-launcher-menu button{display:block;text-align:left;width:100%}.dsh-launcher-menu[data-menu=setup]{right:180px}.dsh-launcher-menu[data-menu=tools]{right:105px}.dsh-launcher-menu[data-menu=about]{right:45px}body{padding-top:32px!important;box-sizing:border-box!important;height:100vh!important;overflow:hidden!important}';document.documentElement.appendChild(s);var h=document.createElement('div');h.id='dsh-launcher-shell';h.innerHTML='<div id="dsh-launcher-drag"><span id="dsh-launcher-title">DeepSeek Harness</span></div><nav id="dsh-launcher-menus"><button data-action="settings">'+t('设置','Settings')+'</button><button data-action="manager">'+t('管理','Manager')+'</button><button data-menu-button="tools">'+t('工具','Tools')+'</button><button data-action="about">'+t('关于','About')+'</button></nav><div id="dsh-launcher-window"><button data-action="minimize">−</button><button data-action="maximize">□</button><button data-action="close">×</button></div><div class="dsh-launcher-menu" data-menu="tools"><button data-action="logs">'+t('日志','Logs')+'</button><button data-action="plugins">'+t('插件管理','Plugins')+'</button><button data-action="ssh">SSH Remote</button><button data-action="restart">'+t('重启 dsh','Restart dsh')+'</button></div>';document.body.appendChild(h);setTitle(document.title||'DeepSeek Harness');installNotifications();document.addEventListener('click',function(e){var t=e.target;if(!t||!t.closest)return;var m=t.closest('[data-menu-button]');if(m){var n=m.getAttribute('data-menu-button');document.querySelectorAll('.dsh-launcher-menu').forEach(function(x){x.classList.toggle('open',x.getAttribute('data-menu')===n)});e.preventDefault();e.stopPropagation();return}var a=t.closest('[data-action]');if(a){document.querySelectorAll('.dsh-launcher-menu').forEach(function(x){x.classList.remove('open')});send(a.getAttribute('data-action'));e.preventDefault();e.stopPropagation();return}if(!t.closest('.dsh-launcher-menu'))document.querySelectorAll('.dsh-launcher-menu').forEach(function(x){x.classList.remove('open')})},true);var drag=document.getElementById('dsh-launcher-drag'),dragTimer=0;drag.addEventListener('mousedown',function(e){if(e.button!==0)return;clearTimeout(dragTimer);if(e.detail>=2){e.preventDefault();return}dragTimer=setTimeout(function(){send('drag')},180)});drag.addEventListener('mouseup',function(){clearTimeout(dragTimer)});drag.addEventListener('mouseleave',function(){clearTimeout(dragTimer)});drag.addEventListener('dblclick',function(e){clearTimeout(dragTimer);send('maximize');e.preventDefault()});theme();new MutationObserver(theme).observe(document.documentElement,{attributes:true,attributeFilter:['class','style']});new MutationObserver(theme).observe(document.body,{attributes:true,attributeFilter:['class','style']});new MutationObserver(function(){theme()}).observe(document.body,{childList:true,subtree:true})}
 */
})();
""";

    public const string NativeChromeGuardScript = "window.__dshLauncherNativeChrome = true;";

    // Native windows no longer inject a client-area shell. Keep only the host bridge
    // needed for notifications and the existing title-update compatibility call.
    public const string HostBridgeScript = """
(function(){
 if(window.__dshLauncherHostBridge)return;
 window.__dshLauncherHostBridge=true;
 function notifyHost(title,opt){try{opt=opt||{};window.chrome.webview.postMessage(JSON.stringify({type:'notification',title:String(title||''),body:String(opt.body||''),tag:opt.tag||null,requireInteraction:!!opt.requireInteraction,icon:opt.icon||null}))}catch(_){} }
 function installNotifications(){if(window.__dshLauncherNotifications)return;window.__dshLauncherNotifications=true;var Native=window.Notification;if(Native){function LauncherNotification(title,opt){if(!(this instanceof LauncherNotification))throw new TypeError('Notification constructor requires new');opt=opt||{};notifyHost(title,opt);var n=new EventTarget();n.title=String(title||'');n.body=String(opt.body||'');n.tag=String(opt.tag||'');n.icon=String(opt.icon||'');n.close=function(){};setTimeout(function(){try{n.dispatchEvent(new Event('show'))}catch(_){}},0);return n}LauncherNotification.permission='granted';LauncherNotification.requestPermission=function(){return Promise.resolve('granted')};LauncherNotification.prototype=Native.prototype;try{window.Notification=LauncherNotification}catch(_){} }if(window.ServiceWorkerRegistration&&window.ServiceWorkerRegistration.prototype&&window.ServiceWorkerRegistration.prototype.showNotification){try{window.ServiceWorkerRegistration.prototype.showNotification=function(title,opt){notifyHost(title,opt||{});return Promise.resolve()}}catch(_){} }}
 // Native title text is driven by CoreWebView2.DocumentTitleChanged; never write document.title back.
 window.__dshLauncherSetTitle=function(){};
 installNotifications();
})();
""";

    public const string NativeThemeScript = """
(function(){
 if(window.__dshLauncherNativeThemeBridge)return;
 window.__dshLauncherNativeThemeBridge=true;
 function transparent(v){return !v||v==='transparent'||v==='rgba(0, 0, 0, 0)'||v==='rgba(0,0,0,0)'}
 function parseColor(v){
  v=String(v||'').trim();
  if(/^#[0-9a-f]{3}$/i.test(v))return [parseInt(v[1]+v[1],16),parseInt(v[2]+v[2],16),parseInt(v[3]+v[3],16)];
  if(/^#[0-9a-f]{6}$/i.test(v))return [parseInt(v.slice(1,3),16),parseInt(v.slice(3,5),16),parseInt(v.slice(5,7),16)];
  var m=v.match(/rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)/i);
  if(m)return [+m[1],+m[2],+m[3]];
  var s=v.match(/color\(\s*srgb\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)/i);
  if(s)return [+s[1]*255,+s[2]*255,+s[3]*255];
  return null;
 }
 function darkOf(v){var c=parseColor(v);return c?((c[0]*299+c[1]*587+c[2]*114)/1000<150):!!(window.matchMedia&&matchMedia('(prefers-color-scheme: dark)').matches)}
 function read(el){if(!el)return null;var s=getComputedStyle(el),bg=s.backgroundColor,fg=s.color;if(transparent(bg)){var r=getComputedStyle(document.documentElement);bg=r.getPropertyValue('--background').trim()||r.getPropertyValue('--bg').trim()||r.getPropertyValue('--color-background').trim()||bg}return {bg:bg,fg:fg}}
 function theme(){
  var r=getComputedStyle(document.documentElement),body=document.body,found=null;
  var candidates=[document.querySelector('#root'),document.querySelector('#app'),body,document.documentElement];
  for(var i=0;i<candidates.length&&!found;i++){var x=read(candidates[i]);if(x&&!transparent(x.bg))found=x}
  if(!found){var p=document.elementFromPoint(Math.max(0,innerWidth/2),Math.max(0,innerHeight/2));while(p&&!found){var y=read(p);if(y&&!transparent(y.bg))found=y;p=p.parentElement}}
  var bg=found&&found.bg,fg=found&&found.fg,dark=darkOf(bg);
  if(transparent(bg))bg=dark?'#1f1f1f':'#ffffff';
  if(transparent(fg))fg=dark?'#f4f4f5':'#242424';
  var accent=r.getPropertyValue('--accent').trim()||r.getPropertyValue('--color-accent').trim()||r.getPropertyValue('--primary').trim()||(dark?'#70a5ff':'#2563eb');
  return {background:bg,foreground:fg,accent:accent,dark:dark}
 }
 function send(){try{var t=theme();window.chrome.webview.postMessage(JSON.stringify({type:'dsh-theme',dark:t.dark,background:t.background,foreground:t.foreground,accent:t.accent}))}catch(_){} }
 var timer=0;function schedule(){if(timer)return;timer=setTimeout(function(){timer=0;send()},80)}
 function start(){send();setTimeout(send,250);setTimeout(send,1000);var o=new MutationObserver(schedule);o.observe(document.documentElement,{subtree:true,attributes:true,attributeFilter:['class','style','data-theme','data-color-mode','data-mode','aria-theme']});window.addEventListener('resize',schedule,{passive:true})}
 if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',start,{once:true});else start();
})();
""";

    // Retained under a distinct name only for source compatibility; never registered by the launcher.
    public const string LegacyNativeThemeScript = """
(function(){
 if(window.__dshLauncherNativeThemeBridge)return;window.__dshLauncherNativeThemeBridge=true;
 function send(){try{var r=getComputedStyle(document.documentElement),bg=r.getPropertyValue('--dsh-launcher-bg').trim()||r.backgroundColor,fg=r.getPropertyValue('--dsh-launcher-fg').trim()||r.color,accent=r.getPropertyValue('--dsh-launcher-accent').trim()||'#60cdff',nums=(bg.match(/\d+(?:\.\d+)?/g)||[]),dark=nums.length>=3?((+nums[0]*299+ +nums[1]*587+ +nums[2]*114)/1000<150):matchMedia('(prefers-color-scheme: dark)').matches;window.chrome.webview.postMessage(JSON.stringify({type:'dsh-theme',dark:dark,background:bg,foreground:fg,accent:accent}))}catch(_){}}
 function start(){send();new MutationObserver(send).observe(document.documentElement,{attributes:true,attributeFilter:['class','style']});if(document.body)new MutationObserver(send).observe(document.body,{attributes:true,attributeFilter:['class','style']})}
 if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',start,{once:true});else start();
})();
""";

    public const string BrowserInteractionScript = """
(function(){
 if(window.__dshLauncherInteractionBridge)return;window.__dshLauncherInteractionBridge=true;
 var Native=window.WebSocket, streams={};
 function post(v){try{window.chrome.webview.postMessage(JSON.stringify(v))}catch(_){}}
 var queue=[],active=null,zh=(navigator.language||'').toLowerCase().indexOf('zh')===0,t=function(c,e){return zh?c:e};
 function esc(v){return String(v==null?'':v).replace(/[&<>'"]/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]})}
 function finish(){if(active&&active.host)active.host.remove();active=null;show()}
 async function reply(item,outcome,error){try{var res=await fetch('api/$events/result',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({type:'client-request',rpcId:'launcher-'+Date.now()+'-'+Math.random(),method:'$events/result',payload:{args:{clientId:item.clientId,eventId:item.eventId,outcome:outcome}}})}),body=JSON.parse(await res.text());if(!res.ok||!body||!body.result||body.result.ok!==true)throw new Error(res.ok?t('dsh 未接受该回答','dsh did not accept this answer'):'HTTP '+res.status);finish()}catch(e){error.textContent=t('提交失败：','Submission failed: ')+(e&&e.message||e);item.busy=false;item.host.querySelectorAll('button').forEach(function(x){x.disabled=false})}}
 function show(){if(active||!queue.length)return;active=queue.shift();var item=active,host=document.createElement('div'),shadow=host.attachShadow({mode:'closed'}),questions=item.request.questions||[];host.id='dsh-launcher-interaction-modal';item.host=host;var styles='<style>:host{all:initial}.back{position:fixed;z-index:2147483647;inset:0;display:flex;align-items:flex-end;justify-content:flex-end;padding:24px;background:#0003;font:14px/1.45 Segoe UI,system-ui,sans-serif;color:#f5f7fb}.card{width:min(560px,calc(100vw - 32px));max-height:calc(100vh - 32px);overflow:auto;box-sizing:border-box;padding:22px;border:1px solid #ffffff22;border-radius:16px;background:#1e1e1e;box-shadow:0 18px 50px #0008}h1{margin:0 0 4px;font-size:18px}.source{font-size:12px;color:#aab2c0;margin-bottom:16px}.question{margin:16px 0}.header,.detail{font-size:13px;color:#b8c0cc}.prompt{font-size:15px;font-weight:700;margin:4px 0 8px}.choice{display:flex;gap:10px;width:100%;border:0;border-radius:10px;padding:8px;background:transparent;color:inherit;text-align:left;cursor:pointer}.choice:hover,.choice.sel{background:#2d3948}.mark{width:18px;height:18px;box-sizing:border-box;flex:0 0 18px;margin-top:2px;border:2px solid #667085;border-radius:50%}.multi .mark{border-radius:5px}.sel .mark{border-color:#4cc2ff;background:#4cc2ff;box-shadow:inset 0 0 0 4px #2d3948}.label{font-weight:600}.desc{font-size:13px;color:#b8c0cc;margin-top:2px}textarea{box-sizing:border-box;width:100%;min-height:72px;resize:vertical;margin-top:10px;padding:10px 12px;border:1px solid #434958;border-radius:10px;outline:none;background:#29292d;color:#f5f7fb;font:inherit}textarea:focus{border-color:#4cc2ff;box-shadow:0 0 0 3px #4cc2ff33}.error{min-height:20px;color:#ffb4ab;margin-top:12px}.actions{display:flex;justify-content:flex-end;gap:8px;margin-top:12px}.action{border:1px solid #454b57;border-radius:9px;background:#29292d;color:#f5f7fb;padding:8px 16px;cursor:pointer;font:inherit}.primary{border-color:#4cc2ff;background:#4cc2ff;color:#062033}.action:disabled{opacity:.55;cursor:wait}@media(max-width:640px){.back{padding:16px}.card{padding:18px}}</style>',html='<div class="back"><section class="card"><h1>'+t('Agent 正在等待你的回答','Agent is waiting for your answer')+'</h1><div class="source">'+t('来源：','Source: ')+esc(item.agentId||t('当前会话','current session'))+'</div>';questions.forEach(function(q,qi){html+='<section class="question" data-q="'+qi+'">'+(q.header?'<div class="header">'+esc(q.header)+'</div>':'')+'<div class="prompt">'+esc(q.question)+'</div>'+(q.detail?'<div class="detail">'+esc(q.detail)+'</div>':'');(q.options||[]).forEach(function(o){html+='<button type="button" class="choice '+(q.multiSelect?'multi':'')+'" data-q="'+qi+'" data-option="'+esc(o.label)+'"><span class="mark"></span><span><div class="label">'+esc(o.label)+'</div>'+(o.description?'<div class="desc">'+esc(o.description)+'</div>':'')+'</span></button>'});html+='<textarea data-custom="'+qi+'" placeholder="'+t('可选：填写自定义回答','Optional: enter a custom answer')+'"></textarea></section>'});html+='<div class="error"></div><div class="actions"><button type="button" class="action" data-cancel>'+t('取消','Cancel')+'</button><button type="button" class="action primary" data-submit>'+t('提交回答','Submit answer')+'</button></div></section></div>';shadow.innerHTML=styles+html;var selected=questions.map(function(){return []}),error=shadow.querySelector('.error');shadow.addEventListener('click',function(e){var b=e.target.closest('.choice');if(b){var qi=+b.dataset.q,label=b.dataset.option,q=questions[qi],arr=selected[qi];if(q.multiSelect){selected[qi]=arr.indexOf(label)>=0?arr.filter(function(x){return x!==label}):arr.concat([label]);b.classList.toggle('sel',selected[qi].indexOf(label)>=0)}else{selected[qi]=[label];shadow.querySelectorAll('.choice[data-q="'+qi+'"]').forEach(function(x){x.classList.toggle('sel',x===b)});var input=shadow.querySelector('[data-custom="'+qi+'"]').value=''}return}if(e.target.closest('[data-cancel]')){item.busy=true;shadow.querySelectorAll('button').forEach(function(x){x.disabled=true});reply(item,{kind:'rejected',error:{name:'UserQuestionError',message:'the user cancelled ask_user_question',code:'ASK_CANCELLED'}},error);return}if(e.target.closest('[data-submit]')){if(item.busy)return;var answers=[],missing=false;questions.forEach(function(q,qi){var custom=shadow.querySelector('[data-custom="'+qi+'"]').value.trim(),sel=custom&&!q.multiSelect?[]:selected[qi];if(!custom&&!sel.length)missing=true;var answer={id:q.id,selected:sel};if(custom)answer.custom=custom;answers.push(answer)});if(missing){error.textContent=t('请为每个问题选择选项或填写回答。','Answer every question before submitting.');return}item.busy=true;shadow.querySelectorAll('button').forEach(function(x){x.disabled=true});reply(item,{kind:'result',value:{answers:answers}},error)}});document.documentElement.appendChild(host)}
 function enqueue(item){queue.push(item);show()}
 function cancel(eventId){if(active&&active.eventId===eventId){finish();return}queue=queue.filter(function(x){return x.eventId!==eventId})}
 // Observe the same event stream as dsh without stopping propagation: dsh's native UI and the optional Launcher overlay are concurrent answer surfaces. The gateway broadcasts a cancel frame after either surface resolves the event, which closes the other surface.
 function mirror(stream,x){if(window.__dshLauncherHandleAgentQuestions!==true)return;var c=stream.clientId;if(!c)return;post({type:'dsh-interaction',action:'request',eventId:x.eventId,clientId:c,agentId:x.agentId||'',event:x.event,request:x.request||{}})}
 function resultEventId(raw,requireEndpoint){try{var m=typeof raw==='string'?JSON.parse(raw):raw;if(!m)return null;if(requireEndpoint&&m.endpoint!=='$events/result'&&m.method!=='$events/result')return null;var a=(m.payload&&m.payload.args)||m.args;return a&&typeof a.eventId==='string'?a.eventId:null}catch(_){return null}}
 function notifyResolved(eventId){if(eventId)post({type:'dsh-interaction',action:'cancel',eventId:eventId})}
 function syncNativeCancel(eventId){if(!eventId)return;Object.keys(streams).forEach(function(id){var stream=streams[id];if(!stream||!stream.ws)return;try{stream.ws.dispatchEvent(new MessageEvent('message',{data:JSON.stringify({type:'item',streamId:id,value:{type:'cancel',eventId:eventId}})}))}catch(_){}})}
 var NativeFetch=window.fetch;if(typeof NativeFetch==='function')try{window.fetch=function(input,init){var url=typeof input==='string'?input:String(input&&(input.url||input.href)||input||''),body=init&&init.body,eventId=String(url||'').indexOf('$events/result')>=0&&typeof body==='string'?resultEventId(body,false):null,result=NativeFetch.apply(this,arguments);if(eventId)Promise.resolve(result).then(function(response){if(response&&response.ok)notifyResolved(eventId)}).catch(function(){});return result}}catch(_){}
 function observe(ws){var send=ws.send.bind(ws);ws.send=function(v){var resolved=resultEventId(v,true);try{var m=JSON.parse(typeof v==='string'?v:'');if(m&&m.type==='open'&&m.endpoint==='$events')streams[m.streamId]={pending:[],ws:ws}}catch(_){}var result=send(v);if(resolved)setTimeout(function(){notifyResolved(resolved)},0);return result};ws.addEventListener('message',function(e){try{var m=JSON.parse(typeof e.data==='string'?e.data:'');if(!m||m.type!=='item'||!streams[m.streamId])return;var stream=streams[m.streamId],x=m.value;if(!x)return;if(x.type==='ready'){stream.clientId=x.clientId;(stream.pending||[]).forEach(function(p){mirror(stream,p)});stream.pending=[];return}if(x.type==='cancel'){stream.pending=(stream.pending||[]).filter(function(p){return p.eventId!==x.eventId});cancel(x.eventId);notifyResolved(x.eventId);return}if(x.type==='waterfall'&&(x.event==='approval/request'||x.event==='user-questions/request')){if(!stream.clientId){stream.pending.push(x);return}mirror(stream,x)}}catch(_){}})}
 function Wrapped(url,protocols){var ws=arguments.length>1?new Native(url,protocols):new Native(url);try{if(String(url).indexOf('/api/remote.mux')>=0)observe(ws)}catch(_){}return ws}
 Wrapped.prototype=Native.prototype;Object.setPrototypeOf(Wrapped,Native);for(var k in Native)try{Wrapped[k]=Native[k]}catch(_){}try{window.WebSocket=Wrapped}catch(_){}
 window.__dshLauncherResolveRemoteEvent=async function(eventId,clientId,outcome,requestId){try{var response=await fetch('api/$events/result',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({type:'client-request',rpcId:'launcher-'+Date.now()+'-'+Math.random(),method:'$events/result',payload:{args:{clientId:clientId,eventId:eventId,outcome:outcome}}})});var text=await response.text();if(!response.ok)throw new Error('dsh 交互结果提交失败: HTTP '+response.status);var reply;try{reply=JSON.parse(text)}catch(_){throw new Error('dsh 返回了无效的交互结果')};if(!reply||!reply.result||reply.result.ok!==true)throw new Error('dsh 未接受交互结果');syncNativeCancel(eventId);post({type:'dsh-interaction-result',requestId:requestId,accepted:true})}catch(error){post({type:'dsh-interaction-result',requestId:requestId,accepted:false,error:String(error&&error.message||error)})}}
})();
""";

    public static string BrowserInteractionConfigScript(bool enabled) =>
        $"window.__dshLauncherHandleAgentQuestions = {(enabled ? "true" : "false")};";

    public const string FilePickerInterceptorScript = """
(function(){
 if(window.__dshLauncherPickerInstalled)return;window.__dshLauncherPickerInstalled=true;
 var pending={};
 function post(v){try{window.chrome.webview.postMessage(JSON.stringify(v))}catch(_){} }
 function urlOf(input){return typeof input==='string'?input:String(input&&(input.url||input.href)||input||'')}
 var nativeFetch=window.fetch;
 if(typeof nativeFetch==='function')try{window.fetch=function(input,init){
  var url=urlOf(input),method=String(init&&init.method||'GET').toUpperCase(),body=init&&init.body,message;
  if(window.__dshLauncherInterceptFilePicker!==true||method!=='POST'||url.indexOf('/api/directoryPicker/pick')<0||typeof body!=='string')return nativeFetch.apply(this,arguments);
  try{message=JSON.parse(body)}catch(_){return nativeFetch.apply(this,arguments)}
  if(!message||message.type!=='client-request'||message.method!=='directoryPicker/pick'||!message.rpcId)return nativeFetch.apply(this,arguments);
  var requestId='launcher-picker-'+Date.now()+'-'+Math.random();
  post({type:'launcher-picker',kind:'directory',requestId:requestId});
  return new Promise(function(resolve){pending[requestId]=function(path){delete pending[requestId];var result={type:'server-response',rpcId:message.rpcId,result:{ok:true,value:path==null?null:String(path)}};resolve(new Response(JSON.stringify(result),{status:200,headers:{'content-type':'application/json'}}))}})
 }}catch(_){}
 window.__dshLauncherResolvePicker=function(requestId,path){var resolve=pending[String(requestId||'')];if(resolve)resolve(path==null?null:String(path))};
})();
""";

    public static string FilePickerConfigScript(bool enabled) =>
        $"window.__dshLauncherInterceptFilePicker = {(enabled ? "true" : "false")};";

}
