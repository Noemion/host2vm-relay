namespace Host2VMRelay;

internal static class AppIcon
{
    public static Icon Load(int pixels)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Host2VMRelay.AppIcon")
            ?? throw new InvalidOperationException("应用图标资源缺失。");
        using var icon = new Icon(stream, pixels, pixels);
        return (Icon)icon.Clone();
    }
}
