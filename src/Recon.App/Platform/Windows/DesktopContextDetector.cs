using System.Runtime.InteropServices;
using System.Text;

namespace Recon.App.Platform.Windows;

internal sealed class DesktopContextDetector
{
    private const uint GuiInMoveSize = 0x00000002;
    private const uint GuiInMenuMode = 0x00000004;
    private const uint GuiSystemMenuMode = 0x00000008;
    private const uint GuiPopupMenuMode = 0x00000010;

    private const uint UnsafeGuiFlags =
        GuiInMoveSize |
        GuiInMenuMode |
        GuiSystemMenuMode |
        GuiPopupMenuMode;

    public bool IsSafeDesktopContext()
    {
        var foregroundWindow = GetForegroundWindow();

        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        var foregroundClass =
            ReadWindowClassName(foregroundWindow);

        if (!IsDesktopWindowClass(foregroundClass))
        {
            return false;
        }

        var threadId = GetWindowThreadProcessId(
            foregroundWindow,
            IntPtr.Zero);

        if (threadId == 0)
        {
            return false;
        }

        var threadInfo = new GuiThreadInfo
        {
            CbSize = (uint)Marshal.SizeOf<GuiThreadInfo>()
        };

        if (!GetGUIThreadInfo(threadId, ref threadInfo))
        {
            return false;
        }

        if ((threadInfo.Flags & UnsafeGuiFlags) != 0)
        {
            return false;
        }

        if (threadInfo.HwndMenuOwner != IntPtr.Zero)
        {
            return false;
        }

        if (threadInfo.HwndMoveSize != IntPtr.Zero)
        {
            return false;
        }

        if (threadInfo.HwndFocus != IntPtr.Zero)
        {
            var focusClass =
                ReadWindowClassName(threadInfo.HwndFocus);

            if (string.IsNullOrWhiteSpace(focusClass))
            {
                return false;
            }

            if (IsEditingControl(focusClass))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDesktopWindowClass(
        string className)
    {
        return string.Equals(
                   className,
                   "Progman",
                   StringComparison.Ordinal)
               ||
               string.Equals(
                   className,
                   "WorkerW",
                   StringComparison.Ordinal);
    }

    private static bool IsEditingControl(
        string className)
    {
        return string.Equals(
                   className,
                   "Edit",
                   StringComparison.OrdinalIgnoreCase)
               ||
               className.Contains(
                   "RichEdit",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadWindowClassName(
        IntPtr windowHandle)
    {
        var buffer = new StringBuilder(256);

        var length = GetClassNameW(
            windowHandle,
            buffer,
            buffer.Capacity);

        return length > 0
            ? buffer.ToString()
            : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint CbSize;
        public uint Flags;
        public IntPtr HwndActive;
        public IntPtr HwndFocus;
        public IntPtr HwndCapture;
        public IntPtr HwndMenuOwner;
        public IntPtr HwndMoveSize;
        public IntPtr HwndCaret;
        public NativeRect RcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport(
        "user32.dll",
        ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport(
        "user32.dll",
        EntryPoint = "GetClassNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
    private static extern int GetClassNameW(
        IntPtr windowHandle,
        StringBuilder className,
        int maximumCount);

    [DllImport(
        "user32.dll",
        ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        IntPtr processId);

    [DllImport(
        "user32.dll",
        SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(
        uint threadId,
        ref GuiThreadInfo guiThreadInfo);
}
