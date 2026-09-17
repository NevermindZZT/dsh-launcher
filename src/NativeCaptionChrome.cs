using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DshLauncher;

internal sealed record CaptionMenuItem(string Text, Action Action);

/// <summary>
/// Native title-bar integration. The standard Windows non-client frame owns
/// drag/resize/Snap behavior; commands are available from the title bar's
/// right-click menu so no second client-area row consumes WebView space.
/// </summary>
internal sealed class NativeCaptionChrome : IDisposable, IMessageFilter
{
    private const int WmNcRButtonUp = 0x00A5;
    private const int WmContextMenu = 0x007B;
    private const int WmSysCommand = 0x0112;
    private const int HtCaption = 2;
    private const int HtSysMenu = 3;
    private const int ScMouseMenu = 0xF090;

    private readonly Form _form;
    private readonly ContextMenuStrip _menu;
    private readonly List<NativeCommand> _nativeCommands = new();
    private int _nextNativeCommand = 0x1F00;
    private ThemeHelper.Palette _palette;
    private bool _pagePaletteApplied;
    private bool _dismissFilterInstalled;
    private bool _disposed;

    private NativeCaptionChrome(Form form, Icon? icon)
    {
        _form = form;
        _palette = ThemeHelper.GetPalette(true);
        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            AutoClose = true,
            Padding = new Padding(6),
            Font = new Font("Segoe UI", 9.5f),
        };
        ConfigureDropDown(_menu);
        _menu.Opening += (_, _) => ApplyMenuPalette();
        _menu.Closed += (_, _) => RemoveDismissFilter();

        form.HandleCreated += (_, _) =>
        {
            ApplySystemFallbackPalette();
            RebuildNativeSystemMenu();
        };
        form.FormClosed += (_, _) => Dispose();
        ApplySystemFallbackPalette();
    }

    public static NativeCaptionChrome Attach(Form form, Icon? icon = null) => new(form, icon);

    /// <summary>Add a top-level command to the title-bar context menu.</summary>
    public void AddButton(string text, Action action)
    {
        if (_disposed) return;
        _menu.Items.Add(CreateMenuItem(text, action));
        RegisterNativeCommand(text, action);
    }

    /// <summary>Add a submenu to the title-bar context menu.</summary>
    public void AddMenuButton(string text, params CaptionMenuItem[] items)
    {
        if (_disposed) return;
        var parent = new ToolStripMenuItem
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(12, 7, 12, 7),
            Margin = new Padding(1),
            ShowShortcutKeys = false,
        };
        ConfigureDropDown(parent.DropDown);
        foreach (var item in items)
        {
            parent.DropDownItems.Add(CreateMenuItem(item.Text, item.Action));
            RegisterNativeCommand(text + " · " + item.Text, item.Action);
        }
        _menu.Items.Add(parent);
    }

    public void AddSeparator()
    {
        if (!_disposed) _menu.Items.Add(new ToolStripSeparator());
    }

    /// <summary>
    /// Called from each native Form.WndProc. Handling this at the Form level is
    /// intentional: WebView2 and the normal WinForms message subclass do not
    /// reliably expose every non-client context-menu message.
    /// </summary>
    internal bool TryHandleWindowMessage(ref Message message)
    {
        if (_disposed) return false;

        if (_menu.Visible && IsPointerDown(message.Msg) && !IsMenuPoint(Cursor.Position))
            _menu.Close(ToolStripDropDownCloseReason.AppFocusChange);

        if (message.Msg == WmNcRButtonUp)
        {
            var hit = unchecked((int)message.WParam.ToInt64());
            if (hit == HtCaption || hit == HtSysMenu)
                return ShowMenu(PointFromLParam(message.LParam));
            return false;
        }

        if (message.Msg == WmContextMenu)
        {
            var point = PointFromLParam(message.LParam);
            if (point.X == -1 && point.Y == -1) point = Cursor.Position;
            if (IsTitleBarPoint(point)) return ShowMenu(point);
            return false;
        }

        if (message.Msg == WmSysCommand)
        {
            var command = (int)(message.WParam.ToInt64() & 0xFFF0L);
            var native = _nativeCommands.FirstOrDefault(x => x.Id == command);
            if (native != null)
            {
                native.Action();
                message.Result = IntPtr.Zero;
                return true;
            }

            // Some Windows versions translate the non-client right click directly
            // into SC_MOUSEMENU without delivering WM_CONTEXTMENU to WinForms.
            if (command == ScMouseMenu) return ShowMenu(Cursor.Position);
        }

        return false;
    }

    private bool ShowMenu(Point point)
    {
        if (point.X == -1 && point.Y == -1) point = Cursor.Position;
        ApplyMenuPalette();
        _menu.Show(point);
        InstallDismissFilter();
        return true;
    }

    private bool IsTitleBarPoint(Point point)
    {
        if (!_form.IsHandleCreated) return false;
        var clientTopLeft = _form.PointToScreen(Point.Empty);
        return point.X >= _form.Left && point.X < _form.Right
            && point.Y >= _form.Top && point.Y < clientTopLeft.Y;
    }

    private ToolStripMenuItem CreateMenuItem(string text, Action action)
    {
        var item = new ToolStripMenuItem
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(12, 7, 12, 7),
            Margin = new Padding(1),
            ShowShortcutKeys = false,
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void ConfigureDropDown(ToolStripDropDown dropDown)
    {
        dropDown.AutoSize = true;
        dropDown.Padding = new Padding(6);
        if (dropDown is ToolStripDropDownMenu menu)
        {
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
        }
        dropDown.Resize += (_, _) => ApplyDropDownShape(dropDown);
        dropDown.Opened += (_, _) =>
        {
            ApplyDropDownShape(dropDown);
            InstallDismissFilter();
        };
    }

    private static void ApplyDropDownShape(ToolStripDropDown dropDown)
    {
        if (dropDown.IsDisposed || dropDown.Width < 4 || dropDown.Height < 4) return;
        var bounds = new Rectangle(0, 0, dropDown.Width - 1, dropDown.Height - 1);
        using var path = CreateRoundedPath(bounds, 9);
        var oldRegion = dropDown.Region;
        dropDown.Region = new Region(path);
        oldRegion?.Dispose();
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void InstallDismissFilter()
    {
        if (_dismissFilterInstalled || _disposed) return;
        Application.AddMessageFilter(this);
        _dismissFilterInstalled = true;
    }

    private void RemoveDismissFilter()
    {
        if (!_dismissFilterInstalled) return;
        Application.RemoveMessageFilter(this);
        _dismissFilterInstalled = false;
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (_disposed || !_menu.Visible || !IsPointerDown(message.Msg)) return false;
        if (!IsMenuPoint(Cursor.Position))
            _menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
        return false;
    }

    private bool IsMenuPoint(Point point)
    {
        if (_menu.Visible && _menu.Bounds.Contains(point)) return true;
        foreach (ToolStripItem item in _menu.Items)
        {
            if (item is ToolStripMenuItem menuItem && IsDropDownPoint(menuItem.DropDown, point)) return true;
        }
        return false;
    }

    private static bool IsDropDownPoint(ToolStripDropDown dropDown, Point point)
    {
        if (dropDown.Visible && dropDown.Bounds.Contains(point)) return true;
        foreach (ToolStripItem item in dropDown.Items)
        {
            if (item is ToolStripMenuItem menuItem && IsDropDownPoint(menuItem.DropDown, point)) return true;
        }
        return false;
    }

    private static bool IsPointerDown(int message)
    {
        return message is 0x0201 or 0x0204 or 0x0207 // client left/right/middle
            or 0x00A1 or 0x00A4 or 0x00A7;          // non-client left/right/middle
    }

    private void RegisterNativeCommand(string text, Action action)
    {
        if (_disposed) return;
        var id = _nextNativeCommand;
        _nextNativeCommand += 0x10;
        _nativeCommands.Add(new NativeCommand(id, text, action));
        RebuildNativeSystemMenu();
    }

    private void RebuildNativeSystemMenu()
    {
        if (_disposed || !_form.IsHandleCreated || _nativeCommands.Count == 0) return;
        var systemMenu = GetSystemMenu(_form.Handle, true);
        if (systemMenu == IntPtr.Zero) return;
        AppendMenu(systemMenu, 0x00000800, UIntPtr.Zero, string.Empty); // MF_SEPARATOR
        foreach (var command in _nativeCommands)
            AppendMenu(systemMenu, 0x00000000, new UIntPtr((uint)command.Id), command.Text); // MF_STRING
    }

    public bool TryApplyThemeMessage(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "dsh-theme") return false;

            var fallbackDark = root.TryGetProperty("dark", out var darkValue)
                && darkValue.ValueKind == JsonValueKind.True;
            var fallback = ThemeHelper.GetPalette(fallbackDark);
            var background = ColorValue(root, "background", fallback.WindowBack);
            var foreground = ColorValue(root, "foreground", fallback.Text);
            var accent = ColorValue(root, "accent", fallback.Accent);
            var dark = background.GetBrightness() < 0.55f;
            var basePalette = ThemeHelper.GetPalette(dark);
            if ((dark && foreground.GetBrightness() < 0.25f) || (!dark && foreground.GetBrightness() > 0.80f))
                foreground = basePalette.Text;

            _pagePaletteApplied = true;
            ApplyPalette(new ThemeHelper.Palette(
                background,
                basePalette.Surface,
                basePalette.SurfaceAlt,
                foreground,
                basePalette.MutedText,
                basePalette.Border,
                accent,
                ThemeHelper.Lighten(accent)));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ApplySystemFallbackPalette()
    {
        if (!_pagePaletteApplied) ApplyPalette(ThemeHelper.GetPalette(true));
    }

    public void ApplyPalette(ThemeHelper.Palette palette)
    {
        if (_disposed) return;
        _palette = palette;
        _form.BackColor = palette.WindowBack;
        ApplyMenuPalette();
        if (_form.IsHandleCreated) ThemeHelper.ApplyTitleBarPalette(_form.Handle, palette);
    }

    private void ApplyMenuPalette()
    {
        if (_disposed) return;
        _menu.Renderer = new ThemeToolStripRenderer(_palette);
        _menu.BackColor = _palette.SurfaceAlt;
        _menu.ForeColor = _palette.Text;
        foreach (ToolStripItem item in _menu.Items) ApplyMenuItemPalette(item);
    }

    private void ApplyMenuItemPalette(ToolStripItem item)
    {
        item.BackColor = _palette.SurfaceAlt;
        item.ForeColor = _palette.Text;
        if (item is ToolStripMenuItem menuItem)
        {
            foreach (ToolStripItem child in menuItem.DropDownItems) ApplyMenuItemPalette(child);
        }
    }

    private static Point PointFromLParam(IntPtr value)
    {
        var packed = value.ToInt64();
        return new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
    }

    private static Color ColorValue(JsonElement root, string property, Color fallback)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return fallback;
        return ParseCssColor(value.GetString() ?? "", fallback);
    }

    private static Color ParseCssColor(string value, Color fallback)
    {
        value = value.Trim();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
            if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
                return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        var numbers = Regex.Matches(value, @"\d+(?:\.\d+)?")
            .Select(x => double.TryParse(x.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0d)
            .Take(3)
            .ToArray();
        if (numbers.Length != 3) return fallback;
        var scale = value.Contains("srgb", StringComparison.OrdinalIgnoreCase) ? 255d : 1d;
        return Color.FromArgb(
            (int)Math.Round(Math.Clamp(numbers[0] * scale, 0d, 255d)),
            (int)Math.Round(Math.Clamp(numbers[1] * scale, 0d, 255d)),
            (int)Math.Round(Math.Clamp(numbers[2] * scale, 0d, 255d)));
    }

    private sealed record NativeCommand(int Id, string Text, Action Action);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    public void Dispose()
    {
        if (_disposed) return;
        RemoveDismissFilter();
        _disposed = true;
        _menu.Dispose();
    }
}
