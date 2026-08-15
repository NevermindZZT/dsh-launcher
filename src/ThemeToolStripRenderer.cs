using System.Drawing;
using System.Windows.Forms;

namespace DshLauncher;

/// <summary>托盘右键菜单的 WinUI 3 风格渲染器：背景/悬停/分隔线/文字全部跟随系统主题色。</summary>
public sealed class ThemeToolStripRenderer : ToolStripProfessionalRenderer
{
    public ThemeToolStripRenderer() : base(new ThemeColorTable()) { }

    private sealed class ThemeColorTable : ProfessionalColorTable
    {
        private static ThemeHelper.Palette P => ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode());

        public override Color ToolStripDropDownBackground => P.SurfaceAlt;
        public override Color ImageMarginGradientBegin => P.SurfaceAlt;
        public override Color ImageMarginGradientMiddle => P.SurfaceAlt;
        public override Color ImageMarginGradientEnd => P.SurfaceAlt;
        public override Color MenuBorder => P.Border;
        public override Color MenuItemBorder => P.Border;
        public override Color MenuItemSelected => P.Surface;
        public override Color MenuItemSelectedGradientBegin => ThemeHelper.Lighten(P.Surface);
        public override Color MenuItemSelectedGradientEnd => ThemeHelper.Lighten(P.Surface);
        public override Color MenuItemPressedGradientBegin => P.Surface;
        public override Color MenuItemPressedGradientMiddle => P.Surface;
        public override Color MenuItemPressedGradientEnd => P.Surface;
        public override Color SeparatorDark => P.Border;
        public override Color SeparatorLight => P.Border;
        public override Color CheckBackground => P.Accent;
        public override Color CheckSelectedBackground => P.Accent;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode()).Text;
        base.OnRenderItemText(e);
    }
}
