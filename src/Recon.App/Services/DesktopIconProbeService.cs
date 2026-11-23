using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Recon.App.Services;

internal sealed class DesktopIconProbeService
{
    public DesktopIconProbeResult Probe()
    {
        var listViewHandle =
            FindDesktopListView();

        if (listViewHandle == IntPtr.Zero)
        {
            return DesktopIconProbeResult.Failure(
                "Windows 바탕화면 아이콘 목록 창을 " +
                "찾지 못했습니다.");
        }

        try
        {
            var listViewElement =
                AutomationElement.FromHandle(
                    listViewHandle);

            var listItemCondition =
                new PropertyCondition(
                    AutomationElement
                        .ControlTypeProperty,
                    ControlType.ListItem);

            var automationItems =
                listViewElement.FindAll(
                    TreeScope.Children,
                    listItemCondition);

            if (automationItems.Count == 0)
            {
                automationItems =
                    listViewElement.FindAll(
                        TreeScope.Descendants,
                        listItemCondition);
            }

            var icons =
                new List<DesktopIconProbeItem>();

            for (var index = 0;
                 index < automationItems.Count;
                 index++)
            {
                try
                {
                    var element =
                        automationItems[index];

                    var current =
                        element.Current;

                    var rectangle =
                        current.BoundingRectangle;

                    if (rectangle.IsEmpty)
                    {
                        continue;
                    }

                    icons.Add(
                        new DesktopIconProbeItem(
                            Index: index,
                            Name:
                                string.IsNullOrWhiteSpace(
                                    current.Name)
                                    ? "(이름 없음)"
                                    : current.Name,
                            AutomationId:
                                current.AutomationId ?? "",
                            X: rectangle.X,
                            Y: rectangle.Y,
                            Width: rectangle.Width,
                            Height: rectangle.Height,
                            IsOffscreen:
                                current.IsOffscreen));
                }
                catch (
                    ElementNotAvailableException)
                {
                    // 검사 도중 사라진 아이콘은 제외합니다.
                }
                catch (InvalidOperationException)
                {
                    // 읽을 수 없는 자동화 요소는 제외합니다.
                }
            }

            var sortedIcons = icons
                .OrderBy(icon => icon.Y)
                .ThenBy(icon => icon.X)
                .ThenBy(icon => icon.Name)
                .ToArray();

            return DesktopIconProbeResult.Success(
                listViewHandle,
                sortedIcons);
        }
        catch (ElementNotAvailableException)
        {
            return DesktopIconProbeResult.Failure(
                "바탕화면 자동화 요소를 사용할 수 없습니다.");
        }
        catch (InvalidOperationException exception)
        {
            return DesktopIconProbeResult.Failure(
                $"바탕화면 아이콘을 읽지 못했습니다: " +
                exception.Message);
        }
    }

    private static IntPtr FindDesktopListView()
    {
        var programManager =
            FindWindowExW(
                IntPtr.Zero,
                IntPtr.Zero,
                "Progman",
                null);

        var shellView =
            programManager == IntPtr.Zero
                ? IntPtr.Zero
                : FindWindowExW(
                    programManager,
                    IntPtr.Zero,
                    "SHELLDLL_DefView",
                    null);

        if (shellView == IntPtr.Zero)
        {
            shellView =
                FindShellViewInsideWorkerWindow();
        }

        if (shellView == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return FindWindowExW(
            shellView,
            IntPtr.Zero,
            "SysListView32",
            null);
    }

    private static IntPtr
        FindShellViewInsideWorkerWindow()
    {
        var result =
            IntPtr.Zero;

        EnumWindowsCallback callback =
            (windowHandle, _) =>
            {
                var shellView =
                    FindWindowExW(
                        windowHandle,
                        IntPtr.Zero,
                        "SHELLDLL_DefView",
                        null);

                if (shellView == IntPtr.Zero)
                {
                    return true;
                }

                result = shellView;

                return false;
            };

        EnumWindows(
            callback,
            IntPtr.Zero);

        return result;
    }

    private delegate bool EnumWindowsCallback(
        IntPtr windowHandle,
        IntPtr parameter);

    [DllImport(
        "user32.dll",
        EntryPoint = "FindWindowExW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
    private static extern IntPtr FindWindowExW(
        IntPtr parentWindow,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [DllImport(
        "user32.dll",
        SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);
}

internal sealed record DesktopIconProbeItem(
    int Index,
    string Name,
    string AutomationId,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsOffscreen);

internal sealed record DesktopIconProbeResult(
    bool IsSuccessful,
    string Reason,
    IntPtr ListViewHandle,
    IReadOnlyList<DesktopIconProbeItem> Icons)
{
    public static DesktopIconProbeResult Success(
        IntPtr listViewHandle,
        IReadOnlyList<DesktopIconProbeItem> icons)
    {
        return new DesktopIconProbeResult(
            IsSuccessful: true,
            Reason:
                icons.Count == 0
                    ? "바탕화면 아이콘이 없거나 " +
                      "UI Automation에서 노출되지 않았습니다."
                    : "바탕화면 아이콘 좌표를 읽었습니다.",
            ListViewHandle: listViewHandle,
            Icons: icons);
    }

    public static DesktopIconProbeResult Failure(
        string reason)
    {
        return new DesktopIconProbeResult(
            IsSuccessful: false,
            Reason: reason,
            ListViewHandle: IntPtr.Zero,
            Icons:
                Array.Empty<DesktopIconProbeItem>());
    }
}
