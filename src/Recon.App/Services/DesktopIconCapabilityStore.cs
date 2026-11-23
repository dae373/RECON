using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Recon.App.Services;

internal sealed class DesktopIconCapabilityStore
{
    private static readonly JsonSerializerOptions
        JsonOptions = new()
        {
            WriteIndented = true,

            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,

            Encoder =
                JavaScriptEncoder.Create(
                    UnicodeRanges.All)
        };

    public async Task<
        DesktopIconCapabilityWriteResult>
        SaveAsync(
            DesktopIconRoundTripResult testResult,
            CancellationToken cancellationToken)
    {
        var primaryScreen =
            Forms.Screen.PrimaryScreen;

        var screenCount =
            Forms.Screen.AllScreens.Length;

        var configurationIsSupported =
            screenCount == 1
            && primaryScreen is not null;

        var verified =
            testResult.IsSuccessful
            && testResult.MoveSucceeded
            && testResult.RestoreSucceeded
            && configurationIsSupported;

        var localAppData =
            Environment.GetFolderPath(
                Environment.SpecialFolder
                    .LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return DesktopIconCapabilityWriteResult
                .Failure(
                    "로컬 애플리케이션 데이터 " +
                    "폴더를 찾을 수 없습니다.");
        }

        var capabilityDirectory =
            Path.Combine(
                localAppData,
                "RECON",
                "capabilities");

        var finalPath =
            Path.Combine(
                capabilityDirectory,
                "desktop-icon-positioning.json");

        var testedAtUtc =
            DateTimeOffset.UtcNow;

        var primaryBounds =
            primaryScreen?.Bounds;

        var document =
            new DesktopIconCapabilityDocument(
                SchemaVersion: 1,
                TestedAtUtc: testedAtUtc,
                IsVerified: verified,

                TestSuccessful:
                    testResult.IsSuccessful,

                MoveSucceeded:
                    testResult.MoveSucceeded,

                RestoreSucceeded:
                    testResult.RestoreSucceeded,

                Reason:
                    testResult.Reason,

                ScreenCount:
                    screenCount,

                PrimaryScreen:
                    primaryBounds is null
                        ? null
                        : new ScreenBoundsRecord(
                            X: primaryBounds.Value.X,
                            Y: primaryBounds.Value.Y,
                            Width:
                                primaryBounds.Value.Width,
                            Height:
                                primaryBounds.Value.Height),

                VirtualScreen:
                    new VirtualScreenRecord(
                        Left:
                            SystemParameters
                                .VirtualScreenLeft,
                        Top:
                            SystemParameters
                                .VirtualScreenTop,
                        Width:
                            SystemParameters
                                .VirtualScreenWidth,
                        Height:
                            SystemParameters
                                .VirtualScreenHeight),

                TestFilePath:
                    testResult.TestFilePath,

                OriginalPosition:
                    new IconPositionRecord(
                        X: testResult.OriginalX,
                        Y: testResult.OriginalY),

                MovedPosition:
                    new IconPositionRecord(
                        X: testResult.MovedX,
                        Y: testResult.MovedY),

                RestoredPosition:
                    new IconPositionRecord(
                        X: testResult.RestoredX,
                        Y: testResult.RestoredY));

        try
        {
            Directory.CreateDirectory(
                capabilityDirectory);

            var temporaryPath =
                Path.Combine(
                    capabilityDirectory,
                    $".desktop-icon-positioning." +
                    $"{Guid.NewGuid():N}.tmp");

            var backupPath =
                finalPath + ".bak";

            var json =
                JsonSerializer.Serialize(
                    document,
                    JsonOptions);

            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
                    new UTF8Encoding(false),
                    cancellationToken);

                if (File.Exists(finalPath))
                {
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }

                    File.Replace(
                        temporaryPath,
                        finalPath,
                        backupPath,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(
                        temporaryPath,
                        finalPath,
                        overwrite: false);
                }
            }
            catch
            {
                TryDelete(
                    temporaryPath);

                throw;
            }

            return new DesktopIconCapabilityWriteResult(
                IsSuccessful: true,
                IsVerified: verified,
                FilePath: finalPath,
                Reason:
                    verified
                        ? "아이콘 이동 및 복원 기능을 " +
                          "단일 모니터 환경에서 검증했습니다."
                        : "테스트 기록은 저장했지만 " +
                          "현재 환경에서는 기능 검증을 " +
                          "완료하지 못했습니다.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DesktopIconCapabilityWriteResult
                .Failure(
                    "기능 검증 기록 저장 실패: " +
                    exception.Message);
        }
    }

    private static void TryDelete(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 원래 저장 오류를 유지합니다.
        }
    }
}

internal sealed record
    DesktopIconCapabilityWriteResult(
        bool IsSuccessful,
        bool IsVerified,
        string? FilePath,
        string Reason)
{
    public static DesktopIconCapabilityWriteResult
        Failure(
            string reason)
    {
        return new DesktopIconCapabilityWriteResult(
            IsSuccessful: false,
            IsVerified: false,
            FilePath: null,
            Reason: reason);
    }
}

internal sealed record
    DesktopIconCapabilityDocument(
        int SchemaVersion,
        DateTimeOffset TestedAtUtc,
        bool IsVerified,
        bool TestSuccessful,
        bool MoveSucceeded,
        bool RestoreSucceeded,
        string Reason,
        int ScreenCount,
        ScreenBoundsRecord? PrimaryScreen,
        VirtualScreenRecord VirtualScreen,
        string TestFilePath,
        IconPositionRecord OriginalPosition,
        IconPositionRecord MovedPosition,
        IconPositionRecord RestoredPosition);

internal sealed record ScreenBoundsRecord(
    int X,
    int Y,
    int Width,
    int Height);

internal sealed record VirtualScreenRecord(
    double Left,
    double Top,
    double Width,
    double Height);

internal sealed record IconPositionRecord(
    double X,
    double Y);
