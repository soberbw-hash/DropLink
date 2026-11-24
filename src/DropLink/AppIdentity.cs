using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DropLink;

/// <summary>
/// The one identity Windows knows this program by: taskbar grouping, the tray
/// balloon, and every toast all resolve through it.
///
/// Without an explicit AppUserModelID the shell invents one from the exe path
/// and caches the icon it finds there. That is why a rebuilt exe kept showing
/// the old icon in notifications while the tray and the window were already
/// correct - the cached entry was keyed by a path that never changed.
/// </summary>
internal static class AppIdentity
{
    internal const string Aumid = "DropLink.Receiver";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private const int ShcneAssocChanged = 0x08000000;

    internal static void Apply()
    {
        try { SetCurrentProcessExplicitAppUserModelID(Aumid); }
        catch (Exception error) { AppLog.Write("aumid not set: " + error.Message); }
        Register();
    }

    /// <summary>
    /// Writes the identity's icon pointer and display name. Done here rather
    /// than only in notify.ps1 because the app-level balloon never runs that
    /// script, so a registration left over from an older build would keep
    /// serving its icon until the first file arrived.
    /// </summary>
    private static void Register()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid);
            if (key is null) return;
            key.SetValue("DisplayName", "DropLink");
            key.SetValue("IconBackgroundColor", "FF1F6FEB");

            // The .ico, not the exe: the shell caches a resolved icon per path,
            // and an IconUri that keeps naming DropLink.exe keeps serving the
            // icon it cached the first time that path was seen.
            //
            // Only a real install has the .ico next to the exe. A dev build or
            // the asInvoker probe runs from somewhere else, and pointing the
            // live registration at a temp folder would break the shipped app's
            // icon the next time it starts.
            var folder = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
            var ico = Path.Combine(folder, "DropLink.ico");
            if (!File.Exists(ico)) return;
            var wanted = ico;

            var current = key.GetValue("IconUri") as string;
            if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return;
            key.SetValue("IconUri", wanted);
            AppLog.Write("aumid icon -> " + wanted);
            // Tell the shell the association data changed so it re-reads the
            // icon instead of serving the cached bitmap.
            SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception error) { AppLog.Write("aumid registration failed: " + error.Message); }
    }
}
