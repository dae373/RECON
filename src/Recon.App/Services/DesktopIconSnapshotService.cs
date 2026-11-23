using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Recon.App.Services;

internal sealed class DesktopIconSnapshotService
{
    private static readonly JsonSerializerOptions
        JsonOptions = new()
        {
            WriteIndented = true,

            Encoder =
                JavaScriptEncoder.Create(
                    UnicodeRanges.All)
        };

    private readonly DesktopIconProbeService
        _probeService = new();

    private readonly DesktopIconCapabilityReaderService
    _capabilityReader = new();

    public async Task<DesktopIconSnapshotWriteResult>
        CaptureAsync(
            string safeCommitPath,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(
                safeCommitPath))
        {
            return DesktopIconSnapshotWriteResult
                .Failure(
                    "Safe Commit 경로가 없습니다.");
        }

        if (!File.Exists(safeCommitPath))
        {
            return DesktopIconSnapshotWriteResult
                .Failure(
                    "Safe Commit 파일을 찾을 수 없습니다.");
        }

        DesktopIconProbeResult probeResult;

        try
        {
            probeResult =
                await Task.Run(
                    _probeService.Probe,
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DesktopIconSnapshotWriteResult
                .Failure(
                    "바탕화면 아이콘 진단에 " +
                    $"실패했습니다: {exception.Message}");
        }

        if (!probeResult.IsSuccessful)
        {
            return DesktopIconSnapshotWriteResult
                .Failure(
                    probeResult.Reason);
        }

        if (probeResult.Icons.Count == 0)
        {
            return DesktopIconSnapshotWriteResult
                .Failure(
                    "저장할 바탕화면 아이콘이 없습니다.");
        }

        var capability =
            await _capabilityReader
                .EvaluateCurrentEnvironmentAsync(
                    cancellationToken);

        try
        {
            var document =
                await LoadDocumentAsync(
                    safeCommitPath,
                    cancellationToken);

            var capturedAtUtc =
                DateTimeOffset.UtcNow;

            var iconItems =
                new JsonArray();

            for (var index = 0;
                 index < probeResult.Icons.Count;
                 index++)
            {
                var icon =
                    probeResult.Icons[index];

                iconItems.Add(
                    new JsonObject
                    {
                        ["itemId"] =
                            $"ICON-{index + 1:D4}",

                        ["name"] =
                            icon.Name,

                        ["automationId"] =
                            icon.AutomationId,

                        ["itemType"] =
                            "unknown",

                        ["protected"] =
                            true,

                        ["x"] =
                            icon.X,

                        ["y"] =
                            icon.Y,

                        ["width"] =
                            icon.Width,

                        ["height"] =
                            icon.Height,

                        ["isOffscreen"] =
                            icon.IsOffscreen
                    });
            }

            var snapshot =
                new JsonObject
                {
                    ["capturedAtUtc"] =
                        capturedAtUtc.ToString("O"),

                    ["captureMethod"] =
                        "UIAutomation.BoundingRectangle",

                    ["coordinateSpace"] =
                        "virtualScreen",

                    ["listViewHandle"] =
                        $"0x{probeResult.ListViewHandle
                            .ToInt64():X}",

                    ["iconCount"] =
                        probeResult.Icons.Count,

                    ["virtualScreen"] =
                        new JsonObject
                        {
                            ["left"] =
                                SystemParameters
                                    .VirtualScreenLeft,

                            ["top"] =
                                SystemParameters
                                    .VirtualScreenTop,

                            ["width"] =
                                SystemParameters
                                    .VirtualScreenWidth,

                            ["height"] =
                                SystemParameters
                                    .VirtualScreenHeight
                        },

                    ["items"] =
                        iconItems
                };

            document["schemaVersion"] =
                Math.Max(
                    ReadSchemaVersion(document),
                    9);

            document[
                "desktopIconSnapshotCaptured"] =
                true;

            document[
                "desktopIconRestoreCapability"] =
                new JsonObject
                {
                    ["isUsable"] =
                        capability.IsUsable,

                    ["reason"] =
                        capability.Reason,

                    ["evidencePath"] =
                        capability.EvidencePath,

                    ["testedAtUtc"] =
                        capability.TestedAtUtc
                            ?.ToString("O")
                };

            document[
            "desktopIconSnapshotReadbackVerified"] =
                false;

            document[
            "desktopIconIdentityMappingCompleted"] =
            false;

            document[
            "desktopIconIdentityMappingSafe"] =
            false;

            document[
                "desktopIconSnapshotCapturedAtUtc"] =
                capturedAtUtc.ToString("O");

            document[
                "desktopIconSnapshot"] =
                snapshot;

            document["updatedAtUtc"] =
                capturedAtUtc.ToString("O");

            await WriteDocumentAtomicallyAsync(
                safeCommitPath,
                document,
                cancellationToken);

            return DesktopIconSnapshotWriteResult
                .Success(
                    probeResult.Icons.Count,
                    safeCommitPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DesktopIconSnapshotWriteResult
                .Failure(
                    "아이콘 스냅샷을 Safe Commit에 " +
                    $"저장하지 못했습니다: " +
                    exception.Message);
        }
    }

    private static async Task<JsonObject>
        LoadDocumentAsync(
            string path,
            CancellationToken cancellationToken)
    {
        var json =
            await File.ReadAllTextAsync(
                path,
                cancellationToken);

        var document =
            JsonNode.Parse(json)
            as JsonObject;

        if (document is null)
        {
            throw new JsonException(
                "Safe Commit JSON 형식이 잘못됐습니다.");
        }

        return document;
    }

    private static async Task
        WriteDocumentAtomicallyAsync(
            string path,
            JsonObject document,
            CancellationToken cancellationToken)
    {
        var directory =
            Path.GetDirectoryName(path);

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "Safe Commit 저장 폴더가 없습니다.");
        }

        var temporaryPath =
            Path.Combine(
                directory,
                $".{Path.GetFileName(path)}." +
                $"{Guid.NewGuid():N}.tmp");

        var backupPath =
            path + ".bak";

        var json =
            document.ToJsonString(
                JsonOptions);

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier:
                        false),
                cancellationToken);

            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            File.Replace(
                temporaryPath,
                path,
                backupPath,
                ignoreMetadataErrors: true);
        }
        catch
        {
            TryDeleteTemporaryFile(
                temporaryPath);

            throw;
        }
    }

    private static int ReadSchemaVersion(
        JsonObject document)
    {
        try
        {
            return document["schemaVersion"]
                ?.GetValue<int>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDeleteTemporaryFile(
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
    DesktopIconSnapshotWriteResult(
        bool IsSuccessful,
        int IconCount,
        string Reason,
        string? FilePath)
{
    public static DesktopIconSnapshotWriteResult
        Success(
            int iconCount,
            string filePath)
    {
        return new DesktopIconSnapshotWriteResult(
            IsSuccessful: true,
            IconCount: iconCount,
            Reason:
                "바탕화면 아이콘 스냅샷을 " +
                "저장했습니다.",
            FilePath: filePath);
    }

    public static DesktopIconSnapshotWriteResult
        Failure(
            string reason)
    {
        return new DesktopIconSnapshotWriteResult(
            IsSuccessful: false,
            IconCount: 0,
            Reason: reason,
            FilePath: null);
    }
}
