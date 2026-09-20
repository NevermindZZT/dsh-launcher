using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher;

/// <summary>Applies an already-received dsh palette to an isolated Launcher WebView.</summary>
internal static class WebThemeBridge
{
    public static void Apply(WebView2 web, ThemeHelper.Palette palette)
    {
        var core = web.CoreWebView2;
        if (core == null) return;

        var payload = JsonSerializer.Serialize(new
        {
            background = Css(palette.WindowBack),
            surface = Css(palette.Surface),
            surfaceAlt = Css(palette.SurfaceAlt),
            text = Css(palette.Text),
            muted = Css(palette.MutedText),
            border = Css(palette.Border),
            accent = Css(palette.Accent),
            accentText = Css(ThemeHelper.ContrastingText(palette.Accent)),
            hover = Css(palette.WindowBack.GetBrightness() < 0.55f ? ThemeHelper.Lighten(palette.Surface, 8) : palette.SurfaceAlt),
            danger = palette.WindowBack.GetBrightness() < 0.55f ? "#ffb4ab" : "#b42318",
            dark = palette.WindowBack.GetBrightness() < 0.55f,
        });
        var script = """
(function(){
 var t=__THEME__,scheme=t.dark?'dark':'light';
 function apply(){
  var root=document.documentElement;
  var vars={'--dsh-launcher-bg':t.background,'--dsh-launcher-fg':t.text,'--dsh-launcher-accent':t.accent,'--bg':t.background,'--layer':t.surface,'--layer2':t.surfaceAlt,'--text':t.text,'--muted':t.muted,'--line':t.border,'--accent':t.accent,'--accentText':t.accentText,'--surface':t.surface,'--surfaceAlt':t.surfaceAlt,'--border':t.border};
  Object.keys(vars).forEach(function(k){root.style.setProperty(k,vars[k])});root.style.colorScheme=scheme;
  var style=document.getElementById('dsh-launcher-theme-bridge');
  if(!style){style=document.createElement('style');style.id='dsh-launcher-theme-bridge';(document.head||document.documentElement).appendChild(style)}
  style.textContent='html,body{background:'+t.background+' !important;color:'+t.text+' !important;color-scheme:'+scheme+' !important}'+
   'body.dsh-interaction{background:'+t.surface+' !important;color:'+t.text+' !important}'+
   'body,.app,main,.content{background:'+t.background+' !important;color:'+t.text+' !important}'+
   'aside,.header,.card,.dialog,.list,.manager-card,.item,.plugin-select{background:'+t.surface+' !important;color:'+t.text+' !important;border-color:'+t.border+' !important}'+
   'input,textarea,select,#dsh-modal input,#dsh-modal textarea,#dsh-modal select,#dsh-modal #logbox{background:'+t.surface+' !important;color:'+t.text+' !important;border-color:'+t.border+' !important}'+
   'button,.button,#dsh-modal button{background:'+t.surfaceAlt+' !important;color:'+t.text+' !important;border-color:'+t.border+' !important}'+
   'button.primary,.button.primary,.primary,#dsh-modal button.primary{background:'+t.accent+' !important;color:'+t.accentText+' !important;border-color:'+t.accent+' !important}'+
   '.entry{background:'+t.surface+' !important;color:'+t.text+' !important;border-color:'+t.border+' !important}'+
   'button:hover,.button:hover,.entry:hover,.nav a:hover{background:'+t.hover+' !important;color:'+t.text+' !important}'+
   'input[type=checkbox],input[type=radio]{accent-color:'+t.accent+' !important}'+
   '.muted,.hint,.subtitle,.manager-muted,.status,.notice,.header,.detail,.source,.desc{color:'+t.muted+' !important}'+
   '.back{background:rgba(0,0,0,'+(t.dark?'.58':'.18')+') !important;color:'+t.text+' !important}'+
   '.card{background:'+t.surface+' !important;color:'+t.text+' !important;border-color:'+t.border+' !important}'+
   '.choice{background:'+t.surface+' !important;color:'+t.text+' !important;border-color:'+t.border+' !important}'+
   '.action{background:'+t.surfaceAlt+' !important;color:'+t.text+' !important;border-color:'+t.border+' !important}'+
   '.choice:hover,.choice.sel{background:'+t.hover+' !important;color:'+t.text+' !important}'+
   'textarea{background:'+t.surface+' !important;color:'+t.text+' !important;border-color:'+t.border+' !important}'+
   '.error{color:'+t.danger+' !important}'+
   '#dsh-modal,#dsh-modal .card,#dsh-modal .standalone{background:'+t.background+' !important;color:'+t.text+' !important;border-color:'+t.border+' !important}'+
   '#dsh-modal .manager-card,#dsh-modal .item,#dsh-modal .plugin-row,#dsh-modal .plugin-select{background:'+t.surface+' !important;color:'+t.text+' !important;border-color:'+t.border+' !important}'+
   '#dsh-modal .plugin-select b,#dsh-modal .plugin-select small{color:'+t.text+' !important}'+
   'html::-webkit-scrollbar,body::-webkit-scrollbar,.card::-webkit-scrollbar,.list::-webkit-scrollbar,#dsh-modal .card::-webkit-scrollbar,#dsh-modal #logbox::-webkit-scrollbar{width:12px;height:12px}'+
   'html::-webkit-scrollbar-track,body::-webkit-scrollbar-track,.card::-webkit-scrollbar-track,.list::-webkit-scrollbar-track,#dsh-modal .card::-webkit-scrollbar-track,#dsh-modal #logbox::-webkit-scrollbar-track{background:'+t.background+' !important}'+
   'html::-webkit-scrollbar-thumb,body::-webkit-scrollbar-thumb,.card::-webkit-scrollbar-thumb,.list::-webkit-scrollbar-thumb,#dsh-modal .card::-webkit-scrollbar-thumb,#dsh-modal #logbox::-webkit-scrollbar-thumb{background:'+t.surfaceAlt+' !important;border:3px solid '+t.background+' !important;border-radius:999px}';
 }
 apply();setTimeout(apply,0);setTimeout(apply,80);
})();
""".Replace("__THEME__", payload, StringComparison.Ordinal);
        _ = core.ExecuteScriptAsync(script);
    }

    private static string Css(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
