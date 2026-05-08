using System.Reflection;

namespace TaskbarResourceMonitor;

internal static class AppIcon
{
    public static Icon Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        // assets/app.ico -> default manifest name: "{AssemblyName}.assets.app.ico"
        var name = asm.GetName().Name + ".assets.app.ico";
        using var s = asm.GetManifestResourceStream(name);
        if (s is null)
        {
            // Fallback: exe associated icon
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath)!; } catch { return SystemIcons.Application; }
        }

        return new Icon(s);
    }
}

