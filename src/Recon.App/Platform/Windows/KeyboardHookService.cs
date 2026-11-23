using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Recon.App.Platform.Windows;

internal sealed class KeyboardHookService : IDisposable
{
    private const int WhKeyboardLowLevel = 13;

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSystemKeyDown = 0x0104;
    private const uint WmSystemKeyUp = 0x0105;

    private const uint VirtualKeyF = 0x46;
    private const uint VirtualKeyControl = 0x11;
    private const uint VirtualKeyLeftControl = 0xA2;
    private const uint VirtualKeyRightControl = 0xA3;

    private readonly DesktopContextDetector
        _desktopContextDetector;

    private readonly LowLevelKeyboardProcedure
        _hookProcedure;

    private IntPtr _hookHandle;

    private bool _genericControlDown;
    private bool _leftControlDown;
    private bool _rightControlDown;
    private bool _fKeyDown;

    private bool _disposed;

    public KeyboardHookService(
        DesktopContextDetector desktopContextDetector)
    {
        _desktopContextDetector =
            desktopContextDetector;

        _hookProcedure = HookCallback;
    }

    public event EventHandler? DesktopCtrlFPressed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        var moduleHandle = GetModuleHandleW(null);

        _hookHandle = SetWindowsHookExW(
            WhKeyboardLowLevel,
            _hookProcedure,
            moduleHandle,
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "키보드 감지기를 설치하지 못했습니다.");
        }
    }

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hookHandle);

        _hookHandle = IntPtr.Zero;

        ResetKeyState();
    }

    private IntPtr HookCallback(
        int code,
        IntPtr messageParameter,
        IntPtr dataParameter)
    {
        if (code < 0)
        {
            return CallNextHookEx(
                _hookHandle,
                code,
                messageParameter,
                dataParameter);
        }

        var message = unchecked(
            (uint)messageParameter.ToInt64());

        var isKeyDown =
            message == WmKeyDown ||
            message == WmSystemKeyDown;

        var isKeyUp =
            message == WmKeyUp ||
            message == WmSystemKeyUp;

        if (!isKeyDown && !isKeyUp)
        {
            return CallNextHookEx(
                _hookHandle,
                code,
                messageParameter,
                dataParameter);
        }

        var keyboardData =
            Marshal.PtrToStructure<KeyboardData>(
                dataParameter);

        UpdateControlState(
            keyboardData.VirtualKeyCode,
            isKeyDown,
            isKeyUp);

        ProcessFKey(
            keyboardData.VirtualKeyCode,
            isKeyDown,
            isKeyUp);

        // 관찰 모드:
        // 어떤 상황에서도 키 입력을 차단하지 않습니다.
        return CallNextHookEx(
            _hookHandle,
            code,
            messageParameter,
            dataParameter);
    }

    private void UpdateControlState(
        uint virtualKeyCode,
        bool isKeyDown,
        bool isKeyUp)
    {
        if (!isKeyDown && !isKeyUp)
        {
            return;
        }

        var value = isKeyDown;

        switch (virtualKeyCode)
        {
            case VirtualKeyControl:
                _genericControlDown = value;
                break;

            case VirtualKeyLeftControl:
                _leftControlDown = value;
                break;

            case VirtualKeyRightControl:
                _rightControlDown = value;
                break;
        }
    }

    private void ProcessFKey(
        uint virtualKeyCode,
        bool isKeyDown,
        bool isKeyUp)
    {
        if (virtualKeyCode != VirtualKeyF)
        {
            return;
        }

        if (isKeyUp)
        {
            _fKeyDown = false;
            return;
        }

        if (!isKeyDown || _fKeyDown)
        {
            return;
        }

        _fKeyDown = true;

        if (!IsControlDown())
        {
            return;
        }

        try
        {
            if (_desktopContextDetector
                .IsSafeDesktopContext())
            {
                DesktopCtrlFPressed?.Invoke(
                    this,
                    EventArgs.Empty);
            }
        }
        catch
        {
            // 후크 콜백에서 오류가 발생해도
            // 원래 키 입력은 계속 전달합니다.
        }
    }

    private bool IsControlDown()
    {
        return _genericControlDown
               || _leftControlDown
               || _rightControlDown;
    }

    private void ResetKeyState()
    {
        _genericControlDown = false;
        _leftControlDown = false;
        _rightControlDown = false;
        _fKeyDown = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();

        _disposed = true;

        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInformation;
    }

    private delegate IntPtr LowLevelKeyboardProcedure(
        int code,
        IntPtr messageParameter,
        IntPtr dataParameter);

    [DllImport(
        "user32.dll",
        EntryPoint = "SetWindowsHookExW",
        SetLastError = true,
        ExactSpelling = true)]
    private static extern IntPtr SetWindowsHookExW(
        int hookType,
        LowLevelKeyboardProcedure callback,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport(
        "user32.dll",
        SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(
        IntPtr hookHandle);

    [DllImport(
        "user32.dll",
        ExactSpelling = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr messageParameter,
        IntPtr dataParameter);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetModuleHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(
        string? moduleName);
}
