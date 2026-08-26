using System.Runtime.InteropServices;
using System.Text.Json;

namespace DshLauncher;

internal static class WebShellBridge
{
    public static bool TryHandleWindowCommand(Form form, string raw, Action<string> action)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "launcher") return false;
            var name = root.TryGetProperty("action", out var value) ? value.GetString() ?? "" : "";
            Diag.Log($"WebShellBridge received action={name}");
            switch (name)
            {
                case "minimize": form.WindowState = FormWindowState.Minimized; return true;
                case "maximize": ToggleMaximize(form); return true;
                case "close": form.Close(); return true;
                case "drag": BeginDrag(form); return true;
                default: action(name); return true;
            }
        }
        catch { return false; }
    }

    public static void ToggleMaximize(Form form)
    {
        if (form.WindowState == FormWindowState.Maximized)
        {
            form.WindowState = FormWindowState.Normal;
            ApplyShape(form);
            return;
        }
        var workArea = Screen.FromHandle(form.Handle).WorkingArea;
        if (form is MainForm main) main.SetWorkAreaMaximizedBounds(workArea);
        else if (form is ConnectionWindow remote) remote.SetWorkAreaMaximizedBounds(workArea);
        form.WindowState = FormWindowState.Maximized;
        ApplyShape(form);
    }

    public static void BeginDrag(Form form)
    {
        ReleaseCapture();
        SendMessage(form.Handle, WM_SYSCOMMAND, SC_MOVE | HTCAPTION, 0);
    }

    public static void ApplyShape(Form form)
    {
        if (!form.IsHandleCreated) return;
        var workArea = Screen.FromHandle(form.Handle).WorkingArea;
        if (form.WindowState == FormWindowState.Maximized || form.Bounds == workArea)
        {
            SetWindowRgn(form.Handle, IntPtr.Zero, true);
            return;
        }
        var radius = 14;
        var region = CreateRoundRectRgn(0, 0, form.Width + 1, form.Height + 1, radius * 2, radius * 2);
        SetWindowRgn(form.Handle, region, true);
    }

    public static void InstallTitleDragSurface(Form form)
    {
        if (form.Controls.OfType<TitleDragPanel>().Any()) return;
        var panel = new TitleDragPanel();
        form.Controls.Add(panel);
        void layout()
        {
            panel.Bounds = new Rectangle(8, 8, Math.Max(0, form.ClientSize.Width - 520), 24);
            panel.BringToFront();
        }
        form.Resize += (_, _) => layout();
        layout();
    }

    public static void InstallResizeGrips(Form form)
    {
        if (form.Controls.OfType<ResizeGripPanel>().Any()) return;
        var grips = new (int hit, Cursor cursor, AnchorStyles anchor)[]
        {
            (4, Cursors.SizeNWSE, AnchorStyles.Top | AnchorStyles.Left),
            (3, Cursors.SizeNS, AnchorStyles.Top),
            (5, Cursors.SizeNESW, AnchorStyles.Top | AnchorStyles.Right),
            (1, Cursors.SizeWE, AnchorStyles.Left),
            (2, Cursors.SizeWE, AnchorStyles.Right),
            (7, Cursors.SizeNESW, AnchorStyles.Bottom | AnchorStyles.Left),
            (6, Cursors.SizeNS, AnchorStyles.Bottom),
            (8, Cursors.SizeNWSE, AnchorStyles.Bottom | AnchorStyles.Right)
        };
        foreach (var item in grips) form.Controls.Add(new ResizeGripPanel(item.hit, item.cursor) { Tag = item.anchor });
        form.Resize += (_, _) => LayoutResizeGrips(form);
        LayoutResizeGrips(form);
    }

    private static void LayoutResizeGrips(Form form)
    {
        const int size = 8;
        var w = form.ClientSize.Width;
        var h = form.ClientSize.Height;
        foreach (Control c in form.Controls)
        {
            if (c is not ResizeGripPanel) continue;
            var a = (AnchorStyles)c.Tag!;
            var left = (a & AnchorStyles.Left) != 0;
            var right = (a & AnchorStyles.Right) != 0;
            var top = (a & AnchorStyles.Top) != 0;
            var bottom = (a & AnchorStyles.Bottom) != 0;
            var corner = (left || right) && (top || bottom);
            var x = right ? w - size : left ? 0 : size;
            var y = bottom ? h - size : top ? 0 : size;
            var cw = corner ? size : (left || right ? size : Math.Max(0, w - size * 2));
            var ch = corner ? size : (top || bottom ? size : Math.Max(0, h - size * 2));
            c.Bounds = new Rectangle(x, y, cw, ch);
            c.BringToFront();
        }
    }

    public static void BeginResize(Form form, int hit)
    {
        if (form.WindowState == FormWindowState.Maximized) return;
        ReleaseCapture();
        SendMessage(form.Handle, WM_SYSCOMMAND, SC_SIZE | hit, 0);
    }

    public static int ResizeHitTest(Form form, Point point)
    {
        if (form.WindowState == FormWindowState.Maximized) return 0;
        const int border = 8;
        var left = point.X <= border;
        var right = point.X >= form.ClientSize.Width - border;
        var top = point.Y <= border;
        var bottom = point.Y >= form.ClientSize.Height - border;
        if (top && left) return 13;
        if (top && right) return 14;
        if (bottom && left) return 16;
        if (bottom && right) return 17;
        if (top) return 12;
        if (bottom) return 15;
        if (left) return 10;
        if (right) return 11;
        return 0;
    }

    private const int WM_NCHITTEST = 0x84;
    private const int WM_SYSCOMMAND = 0x112;
    private const int SC_MOVE = 0xF010;
    private const int SC_SIZE = 0xF000;
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
}
