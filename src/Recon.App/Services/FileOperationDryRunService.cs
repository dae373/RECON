using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace Recon.App.Services;

internal sealed class FileOperationDryRunService
{
    public async Task<FileOperationDryRunResult>
        RunAsync(
            string safeCommitPath,
            CancellationToken cancellationToken)
    {
        if (!File.Exists(safeCommitPath))
        {
            return Failure(
                "Safe Commit 파일을 찾을 수 없습니다.");
        }

        JsonObject document;

        try
        {
            document =
                await LoadDocumentAsync(
                    safeCommitPath,
                    cancellationToken);
        }
        catch (Exception exception)
        {
            return Failure(
                "Safe Commit을 읽지 못했습니다: " +
                exception.Message);
        }

        var status =
            ReadString(
                document,
                "status");

        var canExecute =
            ReadBoolean(
                document,
                "canExecute");

        if (!string.Equals(
                status,
                "ready",
                StringComparison.OrdinalIgnoreCase)
            ||
            !canExecute)
        {
            return Failure(
                "Safe Commit이 실행 준비 상태가 아닙니다.");
        }

        if (Forms.Screen.AllScreens.Length != 1)
        {
            return Failure(
                "현재 모니터가 1대가 아니므로 " +
                "파일 작업을 실행할 수 없습니다.");
        }

        var destinationRoot =
            ReadString(
                document,
                "destinationRoot");

        if (string.IsNullOrWhiteSpace(
                destinationRoot))
        {
            return Failure(
                "Safe Commit에 정리 목적지가 없습니다.");
        }

        if (document["files"] is not
            JsonArray files)
        {
            return Failure(
                "Safe Commit에 파일 목록이 없습니다.");
        }

        var duplicateDestinations =
            files
                .OfType<JsonObject>()
                .Where(IsPlannedFile)
                .Select(file =>
                    NormalizePathSafely(
                        ReadString(
                            file,
                            "plannedPath")))
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path))
                .GroupBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group =>
                    group.Count() > 1)
                .Select(group =>
                    group.Key)
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);

        var results =
            new List<FileOperationDryRunItem>();

        foreach (var file in files
                     .OfType<JsonObject>())
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var result =
                await ValidateFileAsync(
                    file,
                    destinationRoot,
                    duplicateDestinations,
                    cancellationToken);

            results.Add(result);
        }

        var readyCount =
            results.Count(item =>
                item.Status ==
                FileOperationDryRunStatus.Ready);

        var excludedCount =
            results.Count(item =>
                item.Status ==
                FileOperationDryRunStatus.Excluded);

        var blockedCount =
            results.Count(item =>
                item.Status ==
                FileOperationDryRunStatus.Blocked);

        var successful =
            readyCount > 0
            && blockedCount == 0;

        return new FileOperationDryRunResult(
            IsSuccessful: successful,
            Reason:
                successful
                    ? "모든 이동 가능 후보가 최종 " +
                      "드라이런을 통과했습니다."
                    : blockedCount > 0
                        ? "최종 드라이런에서 차단된 " +
                          "파일이 있습니다."
                        : "실행 가능한 파일이 없습니다.",
            Items: results,
            ReadyCount: readyCount,
            ExcludedCount: excludedCount,
            BlockedCount: blockedCount);
    }

    private static async Task<
        FileOperationDryRunItem>
        ValidateFileAsync(
            JsonObject file,
            string destinationRoot,
            IReadOnlySet<string>
                duplicateDestinations,
            CancellationToken cancellationToken)
    {
        var fileId =
            ReadString(
                file,
                "fileId")
            ?? "(ID 없음)";

        var fileName =
            ReadString(
                file,
                "fileName")
            ?? "(이름 없음)";

        var originalPath =
            ReadString(
                file,
                "originalPath")
            ?? "";

        var plannedPath =
            ReadString(
                file,
                "plannedPath")
            ?? "";

        if (!IsPlannedFile(file))
        {
            return Excluded(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "승인된 이동 가능 파일이 아님");
        }

        if (string.IsNullOrWhiteSpace(
                originalPath)
            ||
            string.IsNullOrWhiteSpace(
                plannedPath))
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "원본 또는 목적지 경로가 없음");
        }

        string normalizedOriginalPath;
        string normalizedPlannedPath;
        string normalizedDestinationRoot;

        try
        {
            normalizedOriginalPath =
                Path.GetFullPath(
                    originalPath);

            normalizedPlannedPath =
                Path.GetFullPath(
                    plannedPath);

            normalizedDestinationRoot =
                NormalizeDirectoryPath(
                    destinationRoot);
        }
        catch
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "파일 경로 형식이 잘못됨");
        }

        if (!normalizedPlannedPath.StartsWith(
                normalizedDestinationRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "목적지가 RECON 정리함 외부에 있음");
        }

        if (duplicateDestinations.Contains(
                NormalizePathSafely(
                    normalizedPlannedPath)))
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "여러 파일의 목적지 경로가 동일함");
        }

        if (!File.Exists(
                normalizedOriginalPath))
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "원본 파일이 존재하지 않음");
        }

        if (File.Exists(
                normalizedPlannedPath)
            ||
            Directory.Exists(
                normalizedPlannedPath))
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "목적지에 같은 이름의 항목이 존재함");
        }

        var destinationDriveRoot =
            Path.GetPathRoot(
                normalizedPlannedPath);

        if (string.IsNullOrWhiteSpace(
                destinationDriveRoot)
            ||
            !Directory.Exists(
                destinationDriveRoot))
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "목적지 드라이브에 접근할 수 없음");
        }

        var fileInfo =
            new FileInfo(
                normalizedOriginalPath);

        try
        {
            fileInfo.Refresh();
        }
        catch (Exception exception)
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "현재 파일 정보를 읽지 못함: " +
                exception.Message);
        }

        var scannedSize =
            ReadLong(
                file,
                "scannedSizeBytes");

        if (fileInfo.Length != scannedSize)
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "스캔 후 파일 크기가 변경됨");
        }

        var scannedLastWriteTime =
            ReadDateTime(
                file,
                "scannedLastWriteTimeUtc");

        if (scannedLastWriteTime is null)
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "스캔 당시 수정 시간이 없음");
        }

        if (fileInfo.LastWriteTimeUtc !=
            scannedLastWriteTime.Value)
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "스캔 후 수정 시간이 변경됨");
        }

        var savedHash =
            ReadString(
                file,
                "hash");

        var hashStatus =
            ReadString(
                file,
                "hashStatus");

        if (!string.Equals(
                hashStatus,
                "computed",
                StringComparison.OrdinalIgnoreCase)
            ||
            string.IsNullOrWhiteSpace(savedHash))
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "저장된 SHA-256 해시가 없음");
        }

        string currentHash;

        try
        {
            await using var stream =
                new FileStream(
                    normalizedOriginalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 131072,
                    options:
                        FileOptions.Asynchronous |
                        FileOptions.SequentialScan);

            var hashBytes =
                await SHA256.HashDataAsync(
                    stream,
                    cancellationToken);

            currentHash =
                Convert.ToHexString(
                        hashBytes)
                    .ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is
                IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or CryptographicException)
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "파일이 사용 중이거나 해시를 " +
                $"계산할 수 없음: {exception.Message}");
        }

        if (!string.Equals(
                currentHash,
                savedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "현재 파일 해시가 Safe Commit과 다름");
        }

        fileInfo.Refresh();

        if (fileInfo.Length != scannedSize
            ||
            fileInfo.LastWriteTimeUtc !=
            scannedLastWriteTime.Value)
        {
            return Blocked(
                fileId,
                fileName,
                originalPath,
                plannedPath,
                "최종 검증 중 파일이 변경됨");
        }

        return new FileOperationDryRunItem(
            FileId: fileId,
            FileName: fileName,
            OriginalPath: originalPath,
            PlannedPath: plannedPath,
            Status:
                FileOperationDryRunStatus.Ready,
            Reason:
                "최종 실행 조건을 충족함");
    }

    private static bool IsPlannedFile(
        JsonObject file)
    {
        return ReadBoolean(
                   file,
                   "approved")
               &&
               string.Equals(
                   ReadString(
                       file,
                       "status"),
                   "planned",
                   StringComparison.OrdinalIgnoreCase)
               &&
               string.Equals(
                   ReadString(
                       file,
                       "preflightStatus"),
                   "ready",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static FileOperationDryRunItem
        Excluded(
            string fileId,
            string fileName,
            string originalPath,
            string plannedPath,
            string reason)
    {
        return new FileOperationDryRunItem(
            fileId,
            fileName,
            originalPath,
            plannedPath,
            FileOperationDryRunStatus.Excluded,
            reason);
    }

    private static FileOperationDryRunItem
        Blocked(
            string fileId,
            string fileName,
            string originalPath,
            string plannedPath,
            string reason)
    {
        return new FileOperationDryRunItem(
            fileId,
            fileName,
            originalPath,
            plannedPath,
            FileOperationDryRunStatus.Blocked,
            reason);
    }

    private static FileOperationDryRunResult
        Failure(
            string reason)
    {
        return new FileOperationDryRunResult(
            IsSuccessful: false,
            Reason: reason,
            Items:
                Array.Empty<
                    FileOperationDryRunItem>(),
            ReadyCount: 0,
            ExcludedCount: 0,
            BlockedCount: 0);
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

        return JsonNode.Parse(json)
                   as JsonObject
               ??
               throw new JsonException(
                   "Safe Commit JSON 형식이 " +
                   "올바르지 않습니다.");
    }

    private static string NormalizeDirectoryPath(
        string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            +
            Path.DirectorySeparatorChar;
    }

    private static string NormalizePathSafely(
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string? ReadString(
        JsonObject document,
        string propertyName)
    {
        try
        {
            return document[propertyName]
                ?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadBoolean(
        JsonObject document,
        string propertyName)
    {
        try
        {
            return document[propertyName]
                ?.GetValue<bool>() == true;
        }
        catch
        {
            return false;
        }
    }

    private static long ReadLong(
        JsonObject document,
        string propertyName)
    {
        try
        {
            return document[propertyName]
                ?.GetValue<long>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static DateTime? ReadDateTime(
        JsonObject document,
        string propertyName)
    {
        var value =
            ReadString(
                document,
                propertyName);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
                ? parsed.ToUniversalTime()
                : null;
    }
}

internal enum FileOperationDryRunStatus
{
    Ready,
    Excluded,
    Blocked
}

internal sealed record FileOperationDryRunItem(
    string FileId,
    string FileName,
    string OriginalPath,
    string PlannedPath,
    FileOperationDryRunStatus Status,
    string Reason);

internal sealed record FileOperationDryRunResult(
    bool IsSuccessful,
    string Reason,
    IReadOnlyList<FileOperationDryRunItem> Items,
    int ReadyCount,
    int ExcludedCount,
    int BlockedCount);
