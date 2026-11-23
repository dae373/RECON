using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;

namespace Recon.App.Services;

internal sealed class
    FileOperationDryRunPersistenceService
{
    private static readonly JsonSerializerOptions
        JsonOptions = new()
        {
            WriteIndented = true,

            Encoder =
                JavaScriptEncoder.Create(
                    UnicodeRanges.All)
        };

    public async Task<
        FileOperationDryRunPersistenceResult>
        PersistAsync(
            string safeCommitPath,
            FileOperationDryRunResult dryRunResult,
            CancellationToken cancellationToken)
    {
        if (!File.Exists(safeCommitPath))
        {
            return Failure(
                "Safe Commit 파일을 찾을 수 없습니다.");
        }

        try
        {
            var document =
                await LoadDocumentAsync(
                    safeCommitPath,
                    cancellationToken);

            var currentStatus =
                ReadString(
                    document,
                    "status");

            if (!string.Equals(
                    currentStatus,
                    "ready",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    "ready 상태의 Safe Commit만 " +
                    "드라이런 결과를 저장할 수 있습니다.");
            }

            if (document["files"] is not
                JsonArray files)
            {
                return Failure(
                    "Safe Commit 파일 목록이 없습니다.");
            }

            var dryRunByFileId =
                dryRunResult.Items
                    .GroupBy(
                        item => item.FileId,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First(),
                        StringComparer.OrdinalIgnoreCase);

            foreach (var node in files)
            {
                if (node is not JsonObject file)
                {
                    continue;
                }

                var fileId =
                    ReadString(
                        file,
                        "fileId");

                if (string.IsNullOrWhiteSpace(fileId))
                {
                    continue;
                }

                if (!dryRunByFileId.TryGetValue(
                        fileId,
                        out var dryRunItem))
                {
                    file["dryRunStatus"] =
                        "missing";

                    file["dryRunReason"] =
                        "드라이런 결과를 찾을 수 없음";

                    continue;
                }

                file["dryRunStatus"] =
                    ConvertStatus(
                        dryRunItem.Status);

                file["dryRunReason"] =
                    dryRunItem.Reason;
            }

            var now =
                DateTimeOffset.UtcNow;

            var passed =
                dryRunResult.IsSuccessful
                && dryRunResult.BlockedCount == 0
                && dryRunResult.ReadyCount > 0;

            var nextStatus =
                passed
                    ? "ready"
                    : "validation_failed";

            var canExecute =
                passed;

            var resultReason =
                passed
                    ? "최종 드라이런을 통과했습니다."
                    : "최종 드라이런에서 차단된 " +
                      "파일이 있습니다.";

            document["schemaVersion"] =
                Math.Max(
                    ReadInteger(
                        document,
                        "schemaVersion"),
                    10);

            document["finalDryRunCompleted"] =
                true;

            document["finalDryRunPassed"] =
                passed;

            document["finalDryRunAtUtc"] =
                now.ToString("O");

            document["finalDryRunReadyCount"] =
                dryRunResult.ReadyCount;

            document["finalDryRunExcludedCount"] =
                dryRunResult.ExcludedCount;

            document["finalDryRunBlockedCount"] =
                dryRunResult.BlockedCount;

            document["finalDryRunReason"] =
                dryRunResult.Reason;

            document["status"] =
                nextStatus;

            document["canExecute"] =
                canExecute;

            document["updatedAtUtc"] =
                now.ToString("O");

            document["finalDryRun"] =
                new JsonObject
                {
                    ["completedAtUtc"] =
                        now.ToString("O"),

                    ["passed"] =
                        passed,

                    ["readyCount"] =
                        dryRunResult.ReadyCount,

                    ["excludedCount"] =
                        dryRunResult.ExcludedCount,

                    ["blockedCount"] =
                        dryRunResult.BlockedCount,

                    ["reason"] =
                        dryRunResult.Reason
                };

            var history =
                GetOrCreateDryRunHistory(
                    document);

            history.Add(
                new JsonObject
                {
                    ["atUtc"] =
                        now.ToString("O"),

                    ["passed"] =
                        passed,

                    ["readyCount"] =
                        dryRunResult.ReadyCount,

                    ["excludedCount"] =
                        dryRunResult.ExcludedCount,

                    ["blockedCount"] =
                        dryRunResult.BlockedCount,

                    ["reason"] =
                        dryRunResult.Reason
                });

            if (!passed)
            {
                var stateHistory =
                    GetOrCreateStateHistory(
                        document);

                stateHistory.Add(
                    new JsonObject
                    {
                        ["from"] =
                            currentStatus,

                        ["to"] =
                            "validation_failed",

                        ["atUtc"] =
                            now.ToString("O"),

                        ["reason"] =
                            resultReason
                    });
            }

            await WriteDocumentAtomicallyAsync(
                safeCommitPath,
                document,
                cancellationToken);

            return new
                FileOperationDryRunPersistenceResult(
                    IsSuccessful: true,
                    DryRunPassed: passed,
                    CurrentStatus: nextStatus,
                    CanExecute: canExecute,
                    Reason: resultReason,
                    FilePath: safeCommitPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                "드라이런 결과 저장 실패: " +
                exception.Message);
        }
    }

    private static string ConvertStatus(
        FileOperationDryRunStatus status)
    {
        return status switch
        {
            FileOperationDryRunStatus.Ready =>
                "ready",

            FileOperationDryRunStatus.Excluded =>
                "excluded",

            _ =>
                "blocked"
        };
    }

    private static JsonArray
        GetOrCreateDryRunHistory(
            JsonObject document)
    {
        if (document["finalDryRunHistory"]
            is JsonArray history)
        {
            return history;
        }

        var newHistory =
            new JsonArray();

        document["finalDryRunHistory"] =
            newHistory;

        return newHistory;
    }

    private static JsonArray
        GetOrCreateStateHistory(
            JsonObject document)
    {
        if (document["stateHistory"]
            is JsonArray history)
        {
            return history;
        }

        var newHistory =
            new JsonArray();

        document["stateHistory"] =
            newHistory;

        return newHistory;
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
                new UTF8Encoding(false),
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
            TryDelete(temporaryPath);
            throw;
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

    private static int ReadInteger(
        JsonObject document,
        string propertyName)
    {
        try
        {
            return document[propertyName]
                ?.GetValue<int>() ?? 0;
        }
        catch
        {
            return 0;
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
            // 원래 오류를 유지합니다.
        }
    }

    private static
        FileOperationDryRunPersistenceResult
        Failure(
            string reason)
    {
        return new
            FileOperationDryRunPersistenceResult(
                IsSuccessful: false,
                DryRunPassed: false,
                CurrentStatus:
                    "validation_failed",
                CanExecute: false,
                Reason: reason,
                FilePath: null);
    }
}

internal sealed record
    FileOperationDryRunPersistenceResult(
        bool IsSuccessful,
        bool DryRunPassed,
        string CurrentStatus,
        bool CanExecute,
        string Reason,
        string? FilePath);
