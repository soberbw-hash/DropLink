using System.Security.Principal;

namespace DropLink;

/// <summary>
/// One place that answers "do we hold an administrator token?".
/// </summary>
internal static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
