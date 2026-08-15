using System.Drawing;
using Microsoft.Win32;

namespace DshLauncher;

/// <summary>
/// 子窗口基类：自动应用 DWM 主题（深色/浅色标题栏 + Mica 材质），
/// 控件配色跟随系统主题并在主题切换时实时更新。
/// </summary>
public abstract class ThemedForm : Form
{
    protected ThemedForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterParent;
        Font = UiFont();
    }

    /// <summary>Win11 默认字体 Segoe UI Variable Text 10pt（Win10 回退 Segoe UI）。</summary>
    private static Font UiFont()
    {
        try
        {
            using var probe = new Font("Segoe UI Variable Text", 10f);
            if (string.Equals(probe.Name, "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase))
                return new Font(probe.FontFamily, 10f);
        }
        catch
        {
            // 回退
        }
        return new Font("Segoe UI", 10f);
    }

    protected bool IsDark => ThemeHelper.IsSystemDarkMode();
    protected ThemeHelper.Palette Palette => ThemeHelper.GetPalette(IsDark);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyThemeNow();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.OnHandleDestroyed(e);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(ApplyThemeNow);
            else ApplyThemeNow();
        }
    }

    /// <summary>应用 DWM 主题并刷新控件配色。</summary>
    public void ApplyThemeNow()
    {
        if (!IsHandleCreated) return;
        ThemeHelper.ApplyWindowTheme(Handle, IsDark);
        var p = Palette;
        BackColor = p.WindowBack;
        ForeColor = p.Text;
        ApplyPalette(p);
        Invalidate(true);
    }

    /// <summary>子类实现控件配色（可调用 ApplyPaletteTree 后自定义）。</summary>
    protected abstract void ApplyPalette(ThemeHelper.Palette p);

    /// <summary>按类型递归设置常见控件配色。</summary>
    protected static void ApplyPaletteTree(Control root, ThemeHelper.Palette p)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case ThemedCheckBox tcb:
                    tcb.ApplyPalette(p);
                    break;
                case ThemedRadioButton trb:
                    trb.ApplyPalette(p);
                    break;
                case Label or CheckBox or RadioButton:
                    c.BackColor = Color.Transparent;
                    c.ForeColor = p.Text;
                    break;
                case TextBox t:
                    t.BackColor = p.Surface;
                    t.ForeColor = p.Text;
                    t.BorderStyle = BorderStyle.None;
                    break;
                case RichTextBox rt:
                    rt.BackColor = p.Surface;
                    rt.ForeColor = p.Text;
                    rt.BorderStyle = BorderStyle.None;
                    break;
                case ListView lv:
                    lv.BackColor = p.Surface;
                    lv.ForeColor = p.Text;
                    lv.BorderStyle = BorderStyle.None;
                    break;
                case NumericUpDown n:
                    n.BackColor = p.Surface;
                    n.ForeColor = p.Text;
                    n.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case InputBox ib:
                    ib.BackColor = p.Surface;
                    ib.Inner.BackColor = p.Surface;
                    ib.Inner.ForeColor = p.Text;
                    break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = p.Surface;
                    b.ForeColor = p.Text;
                    b.FlatAppearance.BorderColor = p.Border;
                    b.FlatAppearance.MouseOverBackColor = ThemeHelper.Lighten(p.Surface);
                    b.FlatAppearance.MouseDownBackColor = p.SurfaceAlt;
                    break;
                case Panel or FlowLayoutPanel or TableLayoutPanel or GroupBox:
                    c.BackColor = Color.Transparent;
                    break;
            }
            ApplyPaletteTree(c, p);
        }
    }
}
