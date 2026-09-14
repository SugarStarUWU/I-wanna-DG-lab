namespace IWCoyoteBridge;

internal static class AppIcon
{
    public static Icon Load()
    {
        var assembly = typeof(AppIcon).Assembly;
        var name = assembly.GetManifestResourceNames().Single(x => x.EndsWith("Assets.app.ico", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
