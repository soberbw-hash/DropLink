using System.IO;
using System.Runtime.InteropServices;

namespace DropLink;

internal static class StartMenuIdentity
{
    // The toast header looks up the Start menu entry by its explicit AppUserModelID.
    // The desktop and portable launcher links serve different purposes.
    internal static void Ensure(string exe, string programs, string appId)
    {
        if (string.IsNullOrEmpty(programs)) return;
        Directory.CreateDirectory(programs);
        var path = Path.Combine(programs, "DropLink.lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
        dynamic link = shell.CreateShortcut(path);
        if (File.Exists(path))
        {
            dynamic explorer = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!)!;
            dynamic item = explorer.NameSpace(programs).ParseName(Path.GetFileName(path));
            string? owner = item.ExtendedProperty("System.AppUserModel.ID") as string;
            string target = link.TargetPath;
            bool sameTarget = string.Equals(target, exe, StringComparison.OrdinalIgnoreCase);
            if (owner == appId && sameTarget) return;
            // Leave another application's or the user's unrelated link alone.
            if (owner != appId && (!sameTarget || !string.IsNullOrEmpty(owner))) return;
        }

        var folder = Path.GetDirectoryName(exe)!;
        link.TargetPath = exe;
        link.WorkingDirectory = folder;
        link.IconLocation = Path.Combine(folder, "DropLink.ico") + ",0";
        link.Save();

        var iid = typeof(IPropertyStore).GUID;
        SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out var store);
        var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
        var value = new PropVariant { Type = 31, Text = Marshal.StringToCoTaskMemUni(appId) };
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Text);
            Marshal.ReleaseComObject(store);
        }
        SHChangeNotify(0x2000, 5, path, IntPtr.Zero); // UPDATEITEM, PATHW
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Text;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr context, uint flags,
        ref Guid iid, out IPropertyStore store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int change, uint flags, string path, IntPtr unused);
}
