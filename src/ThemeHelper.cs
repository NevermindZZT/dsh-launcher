using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DshLauncher;

/// <summary>窗口主题与材质：系统深色/浅色检测、DWM 标题栏配色、Mica 背景（Win11 22H2+）。</summary>
public static class ThemeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int BackdropMica = 2;
    private const int BackdropMicaAlt = 4;

    /// <summary>系统当前是否为深色模式（AppsUseLightTheme=0）。</summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>是否支持 Mica 背景（Windows 11 22H2+，build 22621）。</summary>
    public static bool IsMicaSupported()
    {
        try { return Environment.OSVersion.Version.Build >= 22621; }
        catch { return false; }
    }

    /// <summary>应用深色/浅色标题栏。</summary>
    public static void ApplyTitleBarTheme(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int value = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)); } catch { }
    }

    /// <summary>应用 Mica 背景（Win11 22H2+；不支持时静默忽略）。</summary>
    public static void ApplyMica(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsMicaSupported()) return;
        int value = BackdropMica;
        try { DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref value, sizeof(int)); } catch { }
    }

    /// <summary>子窗口完整主题：标题栏配色 + Mica 材质。</summary>
    public static void ApplyWindowTheme(IntPtr hwnd, bool dark)
    {
        ApplyTitleBarTheme(hwnd, dark);
        ApplyMica(hwnd);
    }

    /// <summary>子窗口调色板（跟随系统主题）。</summary>
    public readonly record struct Palette(
        Color WindowBack,   // 窗口背景（Mica 近似色）
        Color Surface,      // 控件表面
        Color SurfaceAlt,   // 次要表面
        Color Text,
        Color MutedText,
        Color Border,
        Color Accent,       // 强调色（选中/勾选）
        Color AccentPressed); // 强调色按下/悬停

    public static Palette GetPalette(bool dark) => dark
        ? new Palette(
            Color.FromArgb(0x1E, 0x1E, 0x1E),
            Color.FromArgb(0x2D, 0x2D, 0x30),
            Color.FromArgb(0x25, 0x25, 0x26),
            Color.FromArgb(0xF0, 0xF0, 0xF0),
            Color.FromArgb(0x9E, 0x9E, 0x9E),
            Color.FromArgb(0x3F, 0x3F, 0x46),
            Color.FromArgb(0x4C, 0xC2, 0xFF),   // 深色强调：亮蓝
            Color.FromArgb(0x99, 0xDB, 0xFF))
        : new Palette(
            Color.FromArgb(0xF3, 0xF3, 0xF3),
            Color.White,
            Color.FromArgb(0xEB, 0xEB, 0xEB),
            Color.FromArgb(0x1A, 0x1A, 0x1A),
            Color.FromArgb(0x60, 0x60, 0x60),
            Color.FromArgb(0xD0, 0xD0, 0xD0),
            Color.FromArgb(0x42, 0x6E, 0xFE),   // 浅色强调：DeepSeek 蓝
            Color.FromArgb(0x33, 0x59, 0xD8));

    /// <summary>颜色变亮（按钮 hover）。</summary>
    public static Color Lighten(Color c, int amount = 18)
    {
        return Color.FromArgb(Math.Min(255, c.R + amount), Math.Min(255, c.G + amount), Math.Min(255, c.B + amount));
    }

    /// <summary>颜色变暗（按钮按下）。</summary>
    public static Color Darken(Color c, int amount = 14)
    {
        return Color.FromArgb(Math.Max(0, c.R - amount), Math.Max(0, c.G - amount), Math.Max(0, c.B - amount));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
