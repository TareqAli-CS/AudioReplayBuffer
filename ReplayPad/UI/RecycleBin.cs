using System.Runtime.InteropServices;

namespace ReplayPad.UI;

/// <summary>Sends files to the Recycle Bin (recoverable) instead of hard-deleting.</summary>
internal static class RecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofAllowUndo = 0x40;
    private const ushort FofNoConfirmation = 0x10;
    private const ushort FofSilent = 0x4;
    private const ushort FofNoErrorUi = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    /// <summary>Throws if the file could not be recycled (e.g. it is in use).</summary>
    public static void Delete(string path)
    {
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = path + "\0", // list must be double-null-terminated
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi
        };
        int result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
            throw new IOException(
                $"Could not delete \"{Path.GetFileName(path)}\" (error {result}). " +
                "If it is playing somewhere, stop playback and try again.");
    }
}
