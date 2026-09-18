using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DshLauncher;

internal sealed record CaptionMenuItem(string Text, Action Action);

/// <summary>
/// Native title-bar integration. The standard Windows non-client frame owns
/// drag/resize/Snap behavior; commands use a dedicated WinForms popup so hover
/// state is reliable instead of depending on ToolStrip selection messages.
/// </summary>
internal sealed class NativeCaptionChrome : IDisposable
{
    private const int WmNcRButtonUp = 0x00A5;
    private const int WmContextMenu = 0x007B;
    private const int WmSysCommand = 0x0112;
    private const int HtCaption = 2;
    private const int HtSysMenu = 3;
    private const int ScMouseMenu = 0xF090;

    private readonly Form _form;
    private readonly List<MenuEntry> _entries = new();
    private readonly List<NativeCommand> _nativeCommands = new();
    private int _nextNativeCommand = 0x1F00;
    private ThemeHelper.Palette _palette;
    private bool _pagePaletteApplied;
    private NativeCaptionMenuWindow? _popup;
    private bool _disposed;

    private NativeCaptionChrome(Form form, Icon? icon)
    {
        _form = form;
        LauncherIconTheme.Attach(form);
        _palette = ThemeHelper.GetPalette(true);
        form.HandleCreated += (_, _) =>
        {
            ApplySystemFallbackPalette();
            RebuildNativeSystemMenu();
        };
        form.FormClosed += (_, _) => Dispose();
        ApplySystemFallbackPalette();
    }

    public static NativeCaptionChrome Attach(Form form, Icon? icon = null) => new(form, icon);

    public void AddButton(string text, Action action)
    {
        if (_disposed) return;
        _entries.Add(new MenuEntry(text, action, null, false));
        RegisterNativeCommand(text, action);
    }

    public void AddMenuButton(string text, params CaptionMenuItem[] items)
    {
        if (_disposed) return;
        var copy = items.ToArray();
        _entries.Add(new MenuEntry(text, null, copy, false));
        foreach (var item in copy) RegisterNativeCommand(text + " · " + item.Text, item.Action);
    }

    public void AddSeparator()
    {
        if (!_disposed) _entries.Add(new MenuEntry(string.Empty, null, null, true));
    }

    internal bool TryHandleWindowMessage(ref Message message)
    {
        if (_disposed) return false;
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
            if (command == ScMouseMenu) return ShowMenu(Cursor.Position);
        }
        return false;
    }

    private bool ShowMenu(Point point)
    {
        if (point.X == -1 && point.Y == -1) point = Cursor.Position;
        ClosePopup();
        var popup = new NativeCaptionMenuWindow(this, _form, _palette, _entries);
        _popup = popup;
        popup.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_popup, popup)) _popup = null;
        };
        try
        {
            popup.ShowAt(point);
        }
        catch (ObjectDisposedException ex)
        {
            Diag.Log("标题栏菜单图标已释放，已忽略本次弹出: " + ex.Message);
            if (!popup.IsDisposed) popup.Dispose();
            if (ReferenceEquals(_popup, popup)) _popup = null;
        }
        catch (InvalidOperationException ex)
        {
            Diag.Log("标题栏菜单创建失败，已忽略本次弹出: " + ex.Message);
            if (!popup.IsDisposed) popup.Dispose();
            if (ReferenceEquals(_popup, popup)) _popup = null;
        }
        return true;
    }

    private void ExecuteMenuAction(Action action)
    {
        ClosePopup();
        action();
    }

    private void ClosePopup()
    {
        if (_popup is { IsDisposed: false }) _popup.Close();
        _popup = null;
    }

    private bool IsTitleBarPoint(Point point)
    {
        if (!_form.IsHandleCreated) return false;
        var clientTopLeft = _form.PointToScreen(Point.Empty);
        return point.X >= _form.Left && point.X < _form.Right
            && point.Y >= _form.Top && point.Y < clientTopLeft.Y;
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
            var pagePalette = new ThemeHelper.Palette(
                background,
                basePalette.Surface,
                basePalette.SurfaceAlt,
                foreground,
                basePalette.MutedText,
                basePalette.Border,
                accent,
                ThemeHelper.Lighten(accent));
            ApplyPalette(pagePalette);
            ThemeHelper.SetPagePalette(pagePalette);
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
        _popup?.ApplyPalette(palette);
        if (_form.IsHandleCreated) ThemeHelper.ApplyTitleBarPalette(_form.Handle, palette);
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
            .Take(3).ToArray();
        if (numbers.Length != 3) return fallback;
        var scale = value.Contains("srgb", StringComparison.OrdinalIgnoreCase) ? 255d : 1d;
        return Color.FromArgb(
            (int)Math.Round(Math.Clamp(numbers[0] * scale, 0d, 255d)),
            (int)Math.Round(Math.Clamp(numbers[1] * scale, 0d, 255d)),
            (int)Math.Round(Math.Clamp(numbers[2] * scale, 0d, 255d)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClosePopup();
    }

    private sealed record MenuEntry(string Text, Action? Action, CaptionMenuItem[]? Children, bool Separator);
    private sealed record NativeCommand(int Id, string Text, Action Action);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    private sealed class NativeCaptionMenuWindow : Form
    {
        private readonly NativeCaptionChrome _owner;
        private readonly Form _ownerForm;
        private readonly FlowLayoutPanel _columns = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(6),
            Margin = new Padding(0),
        };
        private readonly IReadOnlyList<MenuEntry> _rootEntries;
        private ThemeHelper.Palette _palette;
        private IReadOnlyList<CaptionMenuItem>? _activeSubmenu;
        private Point _anchor;

        public NativeCaptionMenuWindow(NativeCaptionChrome owner, Form ownerForm, ThemeHelper.Palette palette, IReadOnlyList<MenuEntry> rootEntries)
        {
            _owner = owner;
            _ownerForm = ownerForm;
            _palette = palette;
            _rootEntries = rootEntries;
            FormBorderStyle = FormBorderStyle.None;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            TopMost = true;
            BackColor = palette.SurfaceAlt;
            Icon = LauncherIconTheme.Load(palette);
            DoubleBuffered = true;
            Controls.Add(_columns);
            Deactivate += (_, _) => { if (!IsDisposed) Close(); };
            Resize += (_, _) => UpdateRegion();
            RebuildLayout();
        }

        public void ShowAt(Point point)
        {
            _anchor = point;
            RebuildLayout();
            var area = Screen.FromPoint(point).WorkingArea;
            Location = ClampLocation(point, area);
            Show(_ownerForm);
            Activate();
        }

        public void ApplyPalette(ThemeHelper.Palette palette)
        {
            _palette = palette;
            BackColor = palette.SurfaceAlt;
            LauncherIconTheme.Apply(this, palette);
            RebuildLayout();
            Invalidate(true);
        }

        private void RebuildLayout()
        {
            _columns.SuspendLayout();
            _columns.Controls.Clear();
            var rootWidth = ColumnWidth(_rootEntries);
            _columns.Controls.Add(CreateColumn(_rootEntries, rootWidth));
            if (_activeSubmenu is { } submenu)
            {
                var separator = new Panel { Width = 6, Height = 1, Margin = new Padding(0), BackColor = Color.Transparent };
                _columns.Controls.Add(separator);
                _columns.Controls.Add(CreateColumn(submenu.Select(x => new MenuEntry(x.Text, x.Action, null, false)).ToArray(), ColumnWidth(submenu)));
            }
            _columns.ResumeLayout(true);
            PerformLayout();
            UpdateRegion();
            if (IsHandleCreated && Visible) Location = ClampLocation(_anchor, Screen.FromPoint(_anchor).WorkingArea);
        }

        private FlowLayoutPanel CreateColumn(IReadOnlyList<MenuEntry> entries, int width)
        {
            var column = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            foreach (var entry in entries)
            {
                if (entry.Separator)
                {
                    column.Controls.Add(new Panel { Width = width - 12, Height = 1, Margin = new Padding(6, 5, 6, 5), BackColor = _palette.Border });
                    continue;
                }
                var hasChildren = entry.Children is { Length: > 0 };
                var button = new Button
                {
                    Text = hasChildren ? entry.Text + "  ›" : entry.Text,
                    Width = width,
                    Height = 36,
                    AutoSize = false,
                    FlatStyle = FlatStyle.Flat,
                    UseVisualStyleBackColor = false,
                    FlatAppearance = { BorderSize = 0 },
                    BackColor = _palette.SurfaceAlt,
                    ForeColor = _palette.Text,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(12, 0, 12, 0),
                    Margin = new Padding(1),
                    TabStop = false,
                };
                var normal = _palette.SurfaceAlt;
                var hover = _palette.WindowBack.GetBrightness() < 0.55f ? ThemeHelper.Lighten(_palette.Surface, 8) : ThemeHelper.Darken(_palette.Surface, 8);
                button.MouseEnter += (_, _) => { button.BackColor = hover; button.Invalidate(); };
                button.MouseLeave += (_, _) => { button.BackColor = normal; button.Invalidate(); };
                if (hasChildren) button.Click += (_, _) => { _activeSubmenu = entry.Children; RebuildLayout(); };
                else if (entry.Action != null) button.Click += (_, _) => _owner.ExecuteMenuAction(entry.Action);
                column.Controls.Add(button);
            }
            return column;
        }

        private int ColumnWidth(IReadOnlyList<MenuEntry> entries)
        {
            var max = 0;
            foreach (var entry in entries)
            {
                var text = entry.Text + (entry.Children is { Length: > 0 } ? "  ›" : "");
                max = Math.Max(max, TextRenderer.MeasureText(text, Font).Width);
            }
            return Math.Max(150, max + 32);
        }

        private int ColumnWidth(IReadOnlyList<CaptionMenuItem> entries) => ColumnWidth(entries.Select(x => new MenuEntry(x.Text, x.Action, null, false)).ToArray());

        private Point ClampLocation(Point point, Rectangle area)
        {
            return new Point(Math.Clamp(point.X, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(point.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }

        private void UpdateRegion()
        {
            if (Width < 4 || Height < 4) return;
            using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 9);
            var old = Region;
            Region = new Region(path);
            old?.Dispose();
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
