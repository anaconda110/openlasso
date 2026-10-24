namespace OpenLasso;

/// <summary>路径解析：优先 %ProgramData%\OpenLasso，无权限时回退 %AppData%\OpenLasso</summary>
public static class AppPaths
{
    public static string BaseDir()
    {
        var pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenLasso");
        try { Directory.CreateDirectory(Path.Combine(pd, "config")); Directory.CreateDirectory(Path.Combine(pd, "logs")); return pd; }
        catch { }
        var ad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenLasso");
        Directory.CreateDirectory(Path.Combine(ad, "config")); Directory.CreateDirectory(Path.Combine(ad, "logs"));
        return ad;
    }
    public static string ConfigPath => Path.Combine(BaseDir(), "config", "openlasso.ini");
    public static string LogDir => Path.Combine(BaseDir(), "logs");
}
