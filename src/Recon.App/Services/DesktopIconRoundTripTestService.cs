using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace Recon.App.Services;

internal sealed class DesktopIconRoundTripTestService
{
    private const string TestFileName =
        "RECON_ICON_POSITION_TEST.txt";

    private const string TestFileNameWithoutExtension =
        "RECON_ICON_POSITION_TEST";

    private const string TestFileMarker =
        "[RECON ICON POSITION TEST]";

    private const int GetWindowStyleIndex = -16;

    private const long ListViewAutoArrangeStyle =
        0x0100;

    private const uint ListViewFirstMessage =
        0x1000;

    private const uint ListViewSetItemPosition =
        ListViewFirstMessage + 15;

    private const uint SendMessageAbortIfHung =
        0x0002;

    private const uint MessageTimeoutMilliseconds =
        2000;

    private const double VerificationTolerance =
        10.0;

    private readonly DesktopIconProbeService
        _probeService = new();

    public async Task<DesktopIconRoundTripResult>
        RunAsync(
            CancellationToken cancellationToken)
    {
        /*
         * 다중 모니터에서는 아이콘 위치를 변경하지 않습니다.
         */
        if (Forms.Screen.AllScreens.Length != 1)
        {
            return Failure(
                "아이콘 이동 테스트는 단일 모니터에서만 " +
                "실행할 수 있습니다.");
        }

        var primaryScreen =
            Forms.Screen.PrimaryScreen;

        if (primaryScreen is null)
        {
            return Failure(
                "주 모니터 정보를 가져올 수 없습니다.");
        }

        /*
         * 실제 Windows 바탕화면 폴더를 가져옵니다.
         */
        var desktopPath =
            Environment.GetFolderPath(
                Environment.SpecialFolder
                    .DesktopDirectory);

        if (string.IsNullOrWhiteSpace(desktopPath))
        {
            return Failure(
                "바탕화면 폴더를 찾을 수 없습니다.");
        }

        if (IsCloudSyncedPath(desktopPath))
        {
            return Failure(
                "현재 Windows 바탕화면이 OneDrive " +
                "동기화 폴더로 설정돼 있습니다. " +
                "RECON은 클라우드 바탕화면에 테스트 " +
                "파일을 자동 생성하지 않습니다.");
        }

        var testFilePath =
            Path.Combine(
                desktopPath,
                TestFileName);

        /*
         * RECON 전용 테스트 파일을 준비합니다.
         */
        var createResult =
            await EnsureTestFileAsync(
                testFilePath,
                cancellationToken);

        if (!createResult.IsSuccessful)
        {
            return Failure(
                createResult.Reason,
                testFilePath);
        }

        /*
         * 바탕화면에서 테스트 아이콘을 찾습니다.
         */
        var initialMatch =
            await WaitForTestIconAsync(
                cancellationToken);

        if (initialMatch is null)
        {
            return Failure(
                "바탕화면에서 테스트 아이콘을 " +
                "찾지 못했습니다.",
                testFilePath);
        }

        var listViewHandle =
            initialMatch.Probe.ListViewHandle;

        if (listViewHandle == IntPtr.Zero)
        {
            return Failure(
                "바탕화면 목록 핸들이 없습니다.",
                testFilePath);
        }

        /*
         * 자동 정렬이 켜져 있으면 중단합니다.
         */
        if (IsAutoArrangeEnabled(
                listViewHandle))
        {
            return Failure(
                "아이콘 자동 정렬이 활성화되어 있습니다. " +
                "자동 정렬을 끈 뒤 다시 실행하세요.",
                testFilePath);
        }

        var originalIcon =
            initialMatch.Icon;

        /*
         * 원래 화면 좌표를 ListView 내부 좌표로 변환합니다.
         */
        var originalScreenPoint =
            new NativePoint(
                (int)Math.Round(originalIcon.X),
                (int)Math.Round(originalIcon.Y));

        var originalClientPoint =
            originalScreenPoint;

        if (!ScreenToClient(
                listViewHandle,
                ref originalClientPoint))
        {
            return Failure(
                "원래 좌표를 바탕화면 내부 좌표로 " +
                "변환하지 못했습니다.",
                testFilePath);
        }

        /*
         * 임시로 이동할 안전한 좌표를 계산합니다.
         */
        var targetScreenPoint =
            CalculateTargetPosition(
                originalIcon,
                primaryScreen.Bounds);

        var targetClientPoint =
            targetScreenPoint;

        if (!ScreenToClient(
                listViewHandle,
                ref targetClientPoint))
        {
            return Failure(
                "테스트 좌표를 바탕화면 내부 좌표로 " +
                "변환하지 못했습니다.",
                testFilePath);
        }

        var restoredSuccessfully = false;

        try
        {
            /*
             * 테스트 아이콘을 임시 위치로 이동합니다.
             */
            var moveSent =
                TrySetItemPosition(
                    listViewHandle,
                    originalIcon.Index,
                    targetClientPoint);

            if (!moveSent)
            {
                return Failure(
                    "Windows가 아이콘 이동 요청을 " +
                    "처리하지 못했습니다.",
                    testFilePath,
                    originalIcon.X,
                    originalIcon.Y);
            }

            await Task.Delay(
                1000,
                cancellationToken);

            /*
             * 이동 후 좌표를 다시 읽습니다.
             */
            var movedMatch =
                await WaitForTestIconAsync(
                    cancellationToken);

            if (movedMatch is null)
            {
                return Failure(
                    "이동 후 테스트 아이콘을 " +
                    "찾지 못했습니다.",
                    testFilePath,
                    originalIcon.X,
                    originalIcon.Y);
            }

            var movedIcon =
                movedMatch.Icon;

            var moveVerified =
                PositionChanged(
                    originalIcon,
                    movedIcon);

            if (!moveVerified)
            {
                return Failure(
                    "아이콘 이동 요청 후 좌표가 " +
                    "변경되지 않았습니다.",
                    testFilePath,
                    originalIcon.X,
                    originalIcon.Y,
                    movedIcon.X,
                    movedIcon.Y);
            }

            /*
             * 테스트 아이콘을 원래 위치로 복원합니다.
             */
            var restoreSent =
                TrySetItemPosition(
                    listViewHandle,
                    originalIcon.Index,
                    originalClientPoint);

            if (!restoreSent)
            {
                return Failure(
                    "원래 위치 복원 요청을 처리하지 " +
                    "못했습니다.",
                    testFilePath,
                    originalIcon.X,
                    originalIcon.Y,
                    movedIcon.X,
                    movedIcon.Y);
            }

            await Task.Delay(
                1000,
                cancellationToken);

            /*
             * 복원된 좌표를 다시 확인합니다.
             */
            var restoredMatch =
                await WaitForTestIconAsync(
                    cancellationToken);

            if (restoredMatch is null)
            {
                return Failure(
                    "복원 후 테스트 아이콘을 " +
                    "찾지 못했습니다.",
                    testFilePath,
                    originalIcon.X,
                    originalIcon.Y,
                    movedIcon.X,
                    movedIcon.Y);
            }

            var restoredIcon =
                restoredMatch.Icon;

            restoredSuccessfully =
                PositionsClose(
                    originalIcon,
                    restoredIcon);

            if (!restoredSuccessfully)
            {
                return Failure(
                    "테스트 아이콘이 원래 좌표로 " +
                    "정확하게 돌아오지 않았습니다.",
                    testFilePath,
                    originalIcon.X,
                    originalIcon.Y,
                    movedIcon.X,
                    movedIcon.Y,
                    restoredIcon.X,
                    restoredIcon.Y);
            }

            return new DesktopIconRoundTripResult(
                IsSuccessful: true,
                MoveSucceeded: true,
                RestoreSucceeded: true,
                Reason:
                    "테스트 아이콘 이동 및 원래 위치 " +
                    "복원에 성공했습니다.",
                TestFilePath:
                    testFilePath,
                OriginalX:
                    originalIcon.X,
                OriginalY:
                    originalIcon.Y,
                MovedX:
                    movedIcon.X,
                MovedY:
                    movedIcon.Y,
                RestoredX:
                    restoredIcon.X,
                RestoredY:
                    restoredIcon.Y);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                "아이콘 이동·복원 테스트 중 오류: " +
                exception.Message,
                testFilePath,
                originalIcon.X,
                originalIcon.Y);
        }
        finally
        {
            /*
             * 성공 여부와 상관없이 원래 위치 복원을
             * 한 번 더 시도합니다.
             */
            if (!restoredSuccessfully)
            {
                TrySetItemPosition(
                    listViewHandle,
                    originalIcon.Index,
                    originalClientPoint);

                try
                {
                    await Task.Delay(
                        600,
                        CancellationToken.None);
                }
                catch
                {
                    // 최종 복구 대기는 무시합니다.
                }
            }
        }
    }

    private async Task<TestIconMatch?>
        WaitForTestIconAsync(
            CancellationToken cancellationToken)
    {
        for (var attempt = 0;
             attempt < 20;
             attempt++)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var probe =
                _probeService.Probe();

            if (probe.IsSuccessful)
            {
                var icon =
                    probe.Icons
                        .FirstOrDefault(
                            IsTestIcon);

                if (icon is not null)
                {
                    return new TestIconMatch(
                        probe,
                        icon);
                }
            }

            await Task.Delay(
                250,
                cancellationToken);
        }

        return null;
    }

    private static bool IsCloudSyncedPath(
    string path)
{
    string normalizedPath;

    try
    {
        normalizedPath =
            Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
            +
            Path.DirectorySeparatorChar;
    }
    catch
    {
        return true;
    }

    var cloudRoots =
        new[]
        {
            Environment.GetEnvironmentVariable(
                "OneDrive"),

            Environment.GetEnvironmentVariable(
                "OneDriveConsumer"),

            Environment.GetEnvironmentVariable(
                "OneDriveCommercial")
        };

    foreach (var cloudRoot in cloudRoots)
    {
        if (string.IsNullOrWhiteSpace(cloudRoot))
        {
            continue;
        }

        string normalizedCloudRoot;

        try
        {
            normalizedCloudRoot =
                Path.GetFullPath(cloudRoot)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)
                +
                Path.DirectorySeparatorChar;
        }
        catch
        {
            continue;
        }

        if (normalizedPath.StartsWith(
                normalizedCloudRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return normalizedPath
        .Split(
            [
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            ],
            StringSplitOptions.RemoveEmptyEntries)
        .Any(part =>
            part.StartsWith(
                "OneDrive",
                StringComparison.OrdinalIgnoreCase));
}

    private static bool IsTestIcon(
        DesktopIconProbeItem icon)
    {
        return string.Equals(
                   icon.Name,
                   TestFileName,
                   StringComparison.OrdinalIgnoreCase)
               ||
               string.Equals(
                   icon.Name,
                   TestFileNameWithoutExtension,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<TestFileCreateResult>
        EnsureTestFileAsync(
            string path,
            CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            try
            {
                var content =
                    await File.ReadAllTextAsync(
                        path,
                        cancellationToken);

                if (!content.StartsWith(
                        TestFileMarker,
                        StringComparison.Ordinal))
                {
                    return new TestFileCreateResult(
                        IsSuccessful: false,
                        Reason:
                            "같은 이름의 파일이 이미 " +
                            "존재하지만 RECON 테스트 " +
                            "파일이 아닙니다.");
                }

                return new TestFileCreateResult(
                    IsSuccessful: true,
                    Reason:
                        "기존 RECON 테스트 파일을 " +
                        "사용합니다.");
            }
            catch (Exception exception)
            {
                return new TestFileCreateResult(
                    IsSuccessful: false,
                    Reason:
                        "기존 테스트 파일 확인 실패: " +
                        exception.Message);
            }
        }

        try
        {
            var content =
                $"""
                {TestFileMarker}

                이 파일은 RECON 바탕화면 아이콘의
                이동 및 복원 기능을 검증하기 위한
                안전한 테스트 파일입니다.

                실제 사용자 데이터는 포함하지 않습니다.
                """;

            await File.WriteAllTextAsync(
                path,
                content,
                cancellationToken);

            return new TestFileCreateResult(
                IsSuccessful: true,
                Reason:
                    "테스트 파일을 생성했습니다.");
        }
        catch (Exception exception)
        {
            return new TestFileCreateResult(
                IsSuccessful: false,
                Reason:
                    "테스트 파일 생성 실패: " +
                    exception.Message);
        }
    }

    private static NativePoint
        CalculateTargetPosition(
            DesktopIconProbeItem icon,
            System.Drawing.Rectangle screenBounds)
    {
        var horizontalDistance =
            Math.Max(
                140,
                (int)Math.Ceiling(
                    icon.Width + 40));

        var verticalDistance =
            Math.Max(
                130,
                (int)Math.Ceiling(
                    icon.Height + 30));

        var targetX =
            (int)Math.Round(icon.X) +
            horizontalDistance;

        var targetY =
            (int)Math.Round(icon.Y) +
            verticalDistance;

        if (targetX + icon.Width >
            screenBounds.Right)
        {
            targetX =
                (int)Math.Round(icon.X) -
                horizontalDistance;
        }

        if (targetY + icon.Height >
            screenBounds.Bottom)
        {
            targetY =
                (int)Math.Round(icon.Y) -
                verticalDistance;
        }

        targetX =
            Math.Clamp(
                targetX,
                screenBounds.Left,
                screenBounds.Right -
                Math.Max(
                    1,
                    (int)icon.Width));

        targetY =
            Math.Clamp(
                targetY,
                screenBounds.Top,
                screenBounds.Bottom -
                Math.Max(
                    1,
                    (int)icon.Height));

        return new NativePoint(
            targetX,
            targetY);
    }

    private static bool TrySetItemPosition(
        IntPtr listViewHandle,
        int itemIndex,
        NativePoint clientPoint)
    {
        var x =
            Math.Clamp(
                clientPoint.X,
                0,
                ushort.MaxValue);

        var y =
            Math.Clamp(
                clientPoint.Y,
                0,
                ushort.MaxValue);

        var packedCoordinates =
            (uint)(ushort)x
            |
            ((uint)(ushort)y << 16);

        var sendResult =
            SendMessageTimeoutW(
                listViewHandle,
                ListViewSetItemPosition,
                new IntPtr(itemIndex),
                new IntPtr(
                    unchecked(
                        (long)packedCoordinates)),
                SendMessageAbortIfHung,
                MessageTimeoutMilliseconds,
                out var messageResult);

        return sendResult != IntPtr.Zero
               &&
               messageResult != UIntPtr.Zero;
    }

    private static bool IsAutoArrangeEnabled(
        IntPtr listViewHandle)
    {
        var style =
            GetWindowStyle(
                listViewHandle);

        return (style &
                ListViewAutoArrangeStyle) != 0;
    }

    private static long GetWindowStyle(
        IntPtr windowHandle)
    {
        if (IntPtr.Size == 8)
        {
            return GetWindowLongPtr64(
                    windowHandle,
                    GetWindowStyleIndex)
                .ToInt64();
        }

        return GetWindowLong32(
            windowHandle,
            GetWindowStyleIndex);
    }

    private static bool PositionChanged(
        DesktopIconProbeItem original,
        DesktopIconProbeItem current)
    {
        return Math.Abs(
                   original.X - current.X)
               > VerificationTolerance
               ||
               Math.Abs(
                   original.Y - current.Y)
               > VerificationTolerance;
    }

    private static bool PositionsClose(
        DesktopIconProbeItem original,
        DesktopIconProbeItem restored)
    {
        return Math.Abs(
                   original.X - restored.X)
               <= VerificationTolerance
               &&
               Math.Abs(
                   original.Y - restored.Y)
               <= VerificationTolerance;
    }

    private static DesktopIconRoundTripResult
        Failure(
            string reason,
            string testFilePath = "",
            double originalX = 0,
            double originalY = 0,
            double movedX = 0,
            double movedY = 0,
            double restoredX = 0,
            double restoredY = 0)
    {
        return new DesktopIconRoundTripResult(
            IsSuccessful: false,

            MoveSucceeded:
                movedX != 0 ||
                movedY != 0,

            RestoreSucceeded:
                false,

            Reason:
                reason,

            TestFilePath:
                testFilePath,

            OriginalX:
                originalX,

            OriginalY:
                originalY,

            MovedX:
                movedX,

            MovedY:
                movedY,

            RestoredX:
                restoredX,

            RestoredY:
                restoredY);
    }

    private sealed record TestIconMatch(
        DesktopIconProbeResult Probe,
        DesktopIconProbeItem Icon);

    private sealed record TestFileCreateResult(
        bool IsSuccessful,
        string Reason);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(
            int x,
            int y)
        {
            X = x;
            Y = y;
        }

        public int X;

        public int Y;
    }

    [DllImport(
        "user32.dll",
        SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(
        IntPtr windowHandle,
        ref NativePoint point);

    [DllImport(
        "user32.dll",
        EntryPoint = "SendMessageTimeoutW",
        SetLastError = true,
        ExactSpelling = true)]
    private static extern IntPtr
        SendMessageTimeoutW(
            IntPtr windowHandle,
            uint message,
            IntPtr wordParameter,
            IntPtr longParameter,
            uint flags,
            uint timeout,
            out UIntPtr result);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowLongPtrW",
        SetLastError = true,
        ExactSpelling = true)]
    private static extern IntPtr
        GetWindowLongPtr64(
            IntPtr windowHandle,
            int index);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowLongW",
        SetLastError = true,
        ExactSpelling = true)]
    private static extern int
        GetWindowLong32(
            IntPtr windowHandle,
            int index);
}

internal sealed record DesktopIconRoundTripResult(
    bool IsSuccessful,
    bool MoveSucceeded,
    bool RestoreSucceeded,
    string Reason,
    string TestFilePath,
    double OriginalX,
    double OriginalY,
    double MovedX,
    double MovedY,
    double RestoredX,
    double RestoredY);
