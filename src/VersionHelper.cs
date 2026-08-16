namespace DshLauncher;

/// <summary>应用版本号（来自程序集版本，csproj Version）。</summary>
public static class VersionHelper
{
    public static string Current =>
        "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0");
}
