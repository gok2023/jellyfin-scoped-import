using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ScopedImport;

[SupportedOSPlatform("windows")]
internal static class WindowsFolderDialog
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint PickFolders = 0x20;
    private const uint ForceFileSystem = 0x40;
    private const uint NoChangeDirectory = 0x8;
    private const uint PathMustExist = 0x800;
    private const uint DontAddToRecent = 0x02000000;
    private const int WtsConnectState = 8;
    private const int WtsActive = 0;
    private static readonly Guid FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    internal static string? Show(string start, CancellationToken token)
    {
        if (!CanOpenDesktop()) throw new InvalidOperationException();
        IFileDialog? dialog = null;
        IShellItem? initial = null;
        IShellItem? selected = null;
        nuint timer = 0;
        TimerCallback? callback = null;
        try
        {
            dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOpenDialog, true)!)!;
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(start, 0, ref iid, out initial));
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | PickFolders | ForceFileSystem | NoChangeDirectory | PathMustExist | DontAddToRecent);
            dialog.SetTitle("选择影视文件夹");
            dialog.SetFolder(initial);
            token.ThrowIfCancellationRequested();
            var raised = false;
            callback = (_, _, _, _) =>
            {
                // The dialog's modal loop dispatches this callback on its owning STA.
                try
                {
                    if (token.IsCancellationRequested) dialog.Close(Cancelled);
                    else if (!raised && GetActiveWindow() is var window && window != 0)
                    {
                        raised = true;
                        if (!SetForegroundWindow(window)) FlashWindow(window, true);
                    }
                }
                catch (COMException) { }
            };
            timer = SetTimer(0, 0, 200, callback);
            if (timer == 0) throw new InvalidOperationException();
            var hr = dialog.Show(0);
            if (hr == Cancelled) return null;
            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out selected);
            selected.GetDisplayName(0x80058000, out var path); // SIGDN_FILESYSPATH
            try { return Marshal.PtrToStringUni(path) ?? throw new InvalidOperationException(); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        finally
        {
            if (timer != 0) KillTimer(0, timer);
            GC.KeepAlive(callback);
            if (selected is not null) Marshal.ReleaseComObject(selected);
            if (initial is not null) Marshal.ReleaseComObject(initial);
            if (dialog is not null) Marshal.ReleaseComObject(dialog);
        }
    }

    private static bool CanOpenDesktop()
    {
        if (!Environment.UserInteractive ||
            !WTSQuerySessionInformationW(0, uint.MaxValue, WtsConnectState, out var state, out var size)) return false;
        try
        {
            if (state == 0 || size < sizeof(int) || Marshal.ReadInt32(state) != WtsActive) return false;
        }
        finally { WTSFreeMemory(state); }
        var input = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
        if (input == 0) return false;
        try
        {
            return DesktopName(input) is "Default" &&
                DesktopName(GetThreadDesktop(GetCurrentThreadId())) is "Default";
        }
        finally { CloseDesktop(input); }
    }

    private static string? DesktopName(nint desktop)
    {
        var name = new StringBuilder(256);
        return desktop != 0 && GetUserObjectInformationW(desktop, 2, name, 512, out _) ? name.ToString() : null;
    }

    // IFileDialog vtable in SDK order. Unused slots are present for ABI alignment.
    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes();
        void SetFileTypeIndex();
        void GetFileTypeIndex();
        void Advise();
        void Unadvise();
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder();
        void SetFolder(IShellItem item);
        void GetFolder();
        void GetCurrentSelection();
        void SetFileName();
        void GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel();
        void SetFileNameLabel();
        void GetResult(out IShellItem item);
        void AddPlace();
        void SetDefaultExtension();
        void Close(int hr);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        void GetDisplayName(uint kind, out nint value);
    }

    private delegate void TimerCallback(nint window, uint message, nuint timer, uint time);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid iid, out IShellItem item);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, TimerCallback callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll")] private static extern nint GetActiveWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool FlashWindow(nint window, bool invert);
    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint session, int infoClass, out nint buffer, out uint size);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder value, uint bytes, out uint needed);
}
