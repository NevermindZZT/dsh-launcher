using System.Drawing;
using System.Windows.Forms;

namespace DshLauncher;

/// <summary>
/// Keeps launcher window and taskbar-button icons readable on both dsh page themes.
/// The dark-theme asset is the existing white whale; the light-theme asset is its
/// dark recolor. A form icon is applied through WM_SETICON by WinForms, so the
/// running taskbar button updates together with the title-bar icon.
/// </summary>
internal static class LauncherIconTheme
{
    private const string DarkThemeResource = "DshLauncher.app.ico";
    private const string LightThemeResource = "DshLauncher.app-light.ico";

    public static bool IsDark(ThemeHelper.Palette palette) => palette.WindowBack.GetBrightness() < 0.55f;

    public static Icon Load(ThemeHelper.Palette palette) => Load(IsDark(palette));

    public static Icon LoadCurrent()
    {
        var palette = ThemeHelper.HasPagePalette
            ? ThemeHelper.CurrentPagePalette
            : ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode());
        return Load(palette);
    }

    public static Icon Load(bool dark)
    {
        var resourceName = dark ? DarkThemeResource : LightThemeResource;
        try
        {
            using var stream = typeof(LauncherIconTheme).Assembly.GetManifestResourceStream(resourceName);
            if (stream != null) return new Icon(stream);
        }
        catch
        {
            // Fall through to a system icon if a packaged variant is unavailable.
        }

        return new Icon(SystemIcons.Application, SystemIcons.Application.Size);
    }

    /// <summary>Attach a themed icon and keep it synchronized with dsh-theme messages.</summary>
    public static void Attach(Form form)
    {
        Apply(form, ThemeHelper.HasPagePalette
            ? ThemeHelper.CurrentPagePalette
            : ThemeHelper.GetPalette(ThemeHelper.IsSystemDarkMode()));

        void OnPaletteChanged(ThemeHelper.Palette palette)
        {
            if (form.IsDisposed) return;
            try
            {
                if (form.IsHandleCreated && form.InvokeRequired)
                    form.BeginInvoke(() => Apply(form, palette));
                else
                    Apply(form, palette);
            }
            catch
            {
                // The form may be closing or its handle may already be gone.
            }
        }

        ThemeHelper.PagePaletteChanged += OnPaletteChanged;
        form.FormClosed += (_, _) =>
        {
            ThemeHelper.PagePaletteChanged -= OnPaletteChanged;
            try
            {
                var icon = form.Icon;
                form.Icon = null;
                icon?.Dispose();
            }
            catch
            {
                // Form disposal is already in progress.
            }
        };
    }

    public static void Apply(Form form, ThemeHelper.Palette palette)
    {
        if (form.IsDisposed) return;
        var next = Load(palette);
        var previous = form.Icon;
        form.Icon = next;
        if (previous != null && !ReferenceEquals(previous, next)) previous.Dispose();
    }

    public static void Apply(NotifyIcon tray, ThemeHelper.Palette palette)
    {
        var next = Load(palette);
        var previous = tray.Icon;
        tray.Icon = next;
        if (previous != null && !ReferenceEquals(previous, next)) previous.Dispose();
    }
}
