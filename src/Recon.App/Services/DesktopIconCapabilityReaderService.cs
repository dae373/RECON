using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Recon.App.Services;

internal sealed class DesktopIconCapabilityReaderService
{
    private const double ScreenTolerance = 1.0;

    private static readonly JsonSerializerOptions
        JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase
        };

    public async Task<
        DesktopIconCapabilityEvaluation>
        EvaluateCurrentEnvironmentAsync(
            CancellationToken cancellationToken)
    {
        var evidencePath =
            GetEvidencePath();

        if (evidencePath is null)
        {
            return Failure(
                "기능 검증 기록 경로를 찾을 수 없습니다.");
        }

        if (!File.Exists(evidencePath))
        {
            return Failure(
                "아이콘 이동·복원 기능 검증 기록이 " +
                "없습니다.",
                evidencePath);
        }

        CapabilityDocument? document;

        try
        {
            var json =
                await File.ReadAllTextAsync(
                    evidencePath,
                    cancellationToken);

            document =
                JsonSerializer.Deserialize<
                    CapabilityDocument>(
                        json,
                        JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                "기능 검증 기록을 읽지 못했습니다: " +
                exception.Message,
                evidencePath);
        }

        if (document is null)
        {
            return Failure(
                "기능 검증 기록이 비어 있습니다.",
                evidencePath);
        }

        if (!document.IsVerified
            || !document.TestSuccessful
            || !document.MoveSucceeded
            || !document.RestoreSucceeded)
        {
            return Failure(
                "아이콘 이동·복원 기능 검증이 " +
                "완료되지 않았습니다.",
                evidencePath,
                document.TestedAtUtc);
        }

        var currentScreenCount =
            Forms.Screen.AllScreens.Length;

        if (currentScreenCount != 1)
        {
            return Failure(
                "현재 모니터가 1대가 아닙니다. " +
                "MVP에서는 아이콘 복원 기능을 " +
                "사용할 수 없습니다.",
                evidencePath,
                document.TestedAtUtc);
        }

        if (document.ScreenCount != 1)
        {
            return Failure(
                "기능 검증 당시 모니터 구성이 " +
                "단일 모니터가 아니었습니다.",
                evidencePath,
                document.TestedAtUtc);
        }

        var primaryScreen =
            Forms.Screen.PrimaryScreen;

        if (primaryScreen is null)
        {
            return Failure(
                "현재 주 모니터 정보를 가져올 수 " +
                "없습니다.",
                evidencePath,
                document.TestedAtUtc);
        }

        if (document.PrimaryScreen is null)
        {
            return Failure(
                "검증 기록에 주 모니터 정보가 없습니다.",
                evidencePath,
                document.TestedAtUtc);
        }

        var currentBounds =
            primaryScreen.Bounds;

        if (document.PrimaryScreen.X !=
                currentBounds.X
            ||
            document.PrimaryScreen.Y !=
                currentBounds.Y
            ||
            document.PrimaryScreen.Width !=
                currentBounds.Width
            ||
            document.PrimaryScreen.Height !=
                currentBounds.Height)
        {
            return Failure(
                "현재 주 모니터 위치 또는 해상도가 " +
                "검증 당시와 다릅니다.",
                evidencePath,
                document.TestedAtUtc);
        }

        if (document.VirtualScreen is null)
        {
            return Failure(
                "검증 기록에 가상 화면 정보가 없습니다.",
                evidencePath,
                document.TestedAtUtc);
        }

        if (!NearlyEqual(
                document.VirtualScreen.Left,
                SystemParameters.VirtualScreenLeft)
            ||
            !NearlyEqual(
                document.VirtualScreen.Top,
                SystemParameters.VirtualScreenTop)
            ||
            !NearlyEqual(
                document.VirtualScreen.Width,
                SystemParameters.VirtualScreenWidth)
            ||
            !NearlyEqual(
                document.VirtualScreen.Height,
                SystemParameters.VirtualScreenHeight))
        {
            return Failure(
                "현재 가상 화면 구성이 검증 당시와 " +
                "다릅니다.",
                evidencePath,
                document.TestedAtUtc);
        }

        return new DesktopIconCapabilityEvaluation(
            IsUsable: true,
            Reason:
                "현재 단일 모니터 구성이 아이콘 " +
                "이동·복원 검증 환경과 일치합니다.",
            EvidencePath:
                evidencePath,
            TestedAtUtc:
                document.TestedAtUtc);
    }

    private static bool NearlyEqual(
        double first,
        double second)
    {
        return Math.Abs(first - second)
               <= ScreenTolerance;
    }

    private static string? GetEvidencePath()
    {
        var localAppData =
            Environment.GetFolderPath(
                Environment.SpecialFolder
                    .LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        return Path.Combine(
            localAppData,
            "RECON",
            "capabilities",
            "desktop-icon-positioning.json");
    }

    private static DesktopIconCapabilityEvaluation
        Failure(
            string reason,
            string? evidencePath = null,
            DateTimeOffset? testedAtUtc = null)
    {
        return new DesktopIconCapabilityEvaluation(
            IsUsable: false,
            Reason: reason,
            EvidencePath: evidencePath,
            TestedAtUtc: testedAtUtc);
    }

    private sealed class CapabilityDocument
    {
        public int SchemaVersion { get; init; }

        public DateTimeOffset TestedAtUtc
        {
            get;
            init;
        }

        public bool IsVerified { get; init; }

        public bool TestSuccessful { get; init; }

        public bool MoveSucceeded { get; init; }

        public bool RestoreSucceeded { get; init; }

        public string? Reason { get; init; }

        public int ScreenCount { get; init; }

        public ScreenBoundsDocument?
            PrimaryScreen
        {
            get;
            init;
        }

        public VirtualScreenDocument?
            VirtualScreen
        {
            get;
            init;
        }

        public string? TestFilePath { get; init; }
    }

    private sealed class ScreenBoundsDocument
    {
        public int X { get; init; }

        public int Y { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }
    }

    private sealed class VirtualScreenDocument
    {
        public double Left { get; init; }

        public double Top { get; init; }

        public double Width { get; init; }

        public double Height { get; init; }
    }
}

internal sealed record
    DesktopIconCapabilityEvaluation(
        bool IsUsable,
        string Reason,
        string? EvidencePath,
        DateTimeOffset? TestedAtUtc);
