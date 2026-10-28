using System.Drawing;

namespace OpenLasso;

internal static class AppIcon
{
    internal const string ResourceName = "OpenLasso.AppIcon.ico";

    internal static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("未找到 OpenLasso 应用图标资源。");
        return new Icon(stream);
    }
}
