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
using Forms = System.Windows.Forms;

namespace Recon.App.Services;

internal sealed class SafeCommitStateService
{
    private static readonly JsonSerializerOptions
        JsonOptions = new()
        {
            WriteIndented = true,

            Encoder =
                JavaScriptEncoder.Create(
                    UnicodeRanges.All)
        };

    private static readonly IReadOnlyDictionary<
        string,
        HashSet<string>>
        AllowedTransitions =
            new Dictionary<
                string,
                HashSet<string>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["draft"] =
                [
                    "validating"
                ],

                ["validation_failed"] =
                [
                    "validating"
                ],

                ["validating"] =
                [
                    "awaiting_icon_snapshot",
                    "ready",
                    "validation_failed"
                ],

                ["awaiting_icon_snapshot"] =
                [
                    "validating",
                    "ready"
                ],

                ["ready"] =
                [
                    "validating",
                    "executing"
                ],

                ["executing"] =
                [
                    "completed",
                    "incomplete",
                    "execution_failed"
                ],

                ["completed"] =
                [
                    "rolling_back"
                ],

                ["incomplete"] =
                [
                    "rolling_back"
                ],

                ["execution_failed"] =
                [
                    "rolling_back"
                ],

                ["rolling_back"] =
                [
                    "rolled_back",
                    "rollback_failed"
                ]
            };

    public async Task<SafeCommitStateResult>
        EvaluateDraftAsync(
            string safeCommitPath,
            CancellationToken cancellationToken)
    {
        var initialDocument =
            await LoadDocumentAsync(
                safeCommitPath,
                cancellationToken);

        var initialStatus =
            GetRequiredString(
                initialDocument,
                "status");

        /*
         * 이미 실행 또는 복구 단계에 들어간 Safe Commit은
         * Draft 검증을 다시 수행하지 않습니다.
         */
        if (initialStatus is
            "executing"
            or "completed"
            or "incomplete"
            or "execution_failed"
            or "rolling_back"
            or "rolled_back"
            or "rollback_failed")
        {
            return CreateCurrentResult(
                initialDocument,
                safeCommitPath,
                "현재 상태에서는 Draft 검증을 " +
                "다시 실행하지 않습니다.");
        }

        /*
         * 이미 아이콘 관련 조건을 기다리는 상태라면
         * 현재 문서를 먼저 평가합니다.
         */
        if (initialStatus is
            "awaiting_icon_snapshot")
        {
            var evaluation =
                EvaluateReadiness(
                    initialDocument);

            /*
             * 아직도 같은 대기 상태라면 불필요한
             * 상태 변경 기록을 추가하지 않습니다.
             */
            if (evaluation.TargetStatus ==
                "awaiting_icon_snapshot")
            {
                return CreateCurrentResult(
                    initialDocument,
                    safeCommitPath,
                    evaluation.Reason);
            }
        }

        /*
         * 이전 실행이 validating 상태에서 중단된 경우
         * 현재 문서를 다시 평가하고 다음 상태로 전환합니다.
         */
        if (initialStatus is "validating")
        {
            var interruptedReadiness =
                EvaluateReadiness(
                    initialDocument);

            return await TransitionAsync(
                safeCommitPath,
                interruptedReadiness.TargetStatus,
                interruptedReadiness.CanExecute,
                interruptedReadiness.Reason,
                cancellationToken);
        }

        /*
         * draft, validation_failed, ready,
         * awaiting_icon_snapshot 상태는 다시 검증합니다.
         */
        await TransitionAsync(
            safeCommitPath,
            newStatus: "validating",
            canExecute: false,
            reason:
                "Safe Commit 실행 조건 검증 시작",
            cancellationToken);

        var validatingDocument =
            await LoadDocumentAsync(
                safeCommitPath,
                cancellationToken);

        var readiness =
            EvaluateReadiness(
                validatingDocument);

        return await TransitionAsync(
            safeCommitPath,
            readiness.TargetStatus,
            readiness.CanExecute,
            readiness.Reason,
            cancellationToken);
    }

    private static ReadinessEvaluation
        EvaluateReadiness(
            JsonObject document)
    {
        /*
         * 1. 이동 전 검증 확인
         */
        if (!GetBoolean(
                document,
                "preflightValidated"))
        {
            return new ReadinessEvaluation(
                "validation_failed",
                false,
                "이동 전 검증이 완료되지 않음");
        }

        /*
         * 2. Safe Commit 파일 목록 확인
         */
        if (document["files"] is not
            JsonArray files)
        {
            return new ReadinessEvaluation(
                "validation_failed",
                false,
                "Safe Commit 파일 목록이 없음");
        }

        /*
         * 3. 이동 가능 파일만 가져오기
         */
        var readyFiles = files
            .OfType<JsonObject>()
            .Where(file =>
                string.Equals(
                    GetOptionalString(
                        file,
                        "preflightStatus"),
                    "ready",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (readyFiles.Length == 0)
        {
            return new ReadinessEvaluation(
                "validation_failed",
                false,
                "이동 가능한 파일이 없음");
        }

        /*
         * 4. 이동 가능 파일의 SHA-256 확인
         */
        foreach (var file in readyFiles)
        {
            var hashStatus =
                GetOptionalString(
                    file,
                    "hashStatus");

            var hash =
                GetOptionalString(
                    file,
                    "hash");

            if (!string.Equals(
                    hashStatus,
                    "computed",
                    StringComparison.OrdinalIgnoreCase)
                ||
                string.IsNullOrWhiteSpace(hash))
            {
                var fileName =
                    GetOptionalString(
                        file,
                        "fileName")
                    ?? "알 수 없는 파일";

                return new ReadinessEvaluation(
                    "validation_failed",
                    false,
                    $"{fileName}의 SHA-256 해시가 " +
                    "완료되지 않음");
            }
        }

        /*
        * 이전 최종 드라이런이 실패했다면
        * 실행 가능 상태로 되돌리지 않습니다.
        */
        if (GetBoolean(
                document,
                "finalDryRunCompleted")
            &&
            !GetBoolean(
                document,
                "finalDryRunPassed"))
        {
            return new ReadinessEvaluation(
                "validation_failed",
                false,
                "이전 최종 드라이런이 실패함");
        }

        /*
         * 5. MVP 단일 모니터 조건 확인
         */
        if (Forms.Screen.AllScreens.Length != 1)
        {
            return new ReadinessEvaluation(
                "awaiting_icon_snapshot",
                false,
                "현재 모니터가 1대가 아니므로 " +
                "아이콘 복원 기능을 사용할 수 없음");
        }

        /*
         * 6. 아이콘 좌표 저장 확인
         */
        if (!GetBoolean(
                document,
                "desktopIconSnapshotCaptured"))
        {
            return new ReadinessEvaluation(
                "awaiting_icon_snapshot",
                false,
                "바탕화면 아이콘 좌표 저장 대기 중");
        }

        /*
         * 7. 아이콘 좌표 재조회 검증 확인
         */
        if (!GetBoolean(
                document,
                "desktopIconSnapshotReadbackVerified"))
        {
            return new ReadinessEvaluation(
                "awaiting_icon_snapshot",
                false,
                "바탕화면 아이콘 좌표 재조회 " +
                "검증 대기 중");
        }

        /*
         * 8. 아이콘과 실제 바탕화면 항목 연결 확인
         */
        if (!GetBoolean(
                document,
                "desktopIconIdentityMappingCompleted"))
        {
            return new ReadinessEvaluation(
                "awaiting_icon_snapshot",
                false,
                "바탕화면 아이콘과 실제 파일 연결 " +
                "대기 중");
        }

        /*
         * 9. 모호한 아이콘 연결 확인
         */
        if (!GetBoolean(
                document,
                "desktopIconIdentityMappingSafe"))
        {
            return new ReadinessEvaluation(
                "awaiting_icon_snapshot",
                false,
                "모호한 바탕화면 아이콘 연결이 있음");
        }

        /*
         * 10. 실제 아이콘 이동·복원 검증 확인
         */
        if (!GetBoolean(
                document,
                "desktopIconSnapshotVerified"))
        {
            return new ReadinessEvaluation(
                "awaiting_icon_snapshot",
                false,
                "바탕화면 아이콘 좌표 복원 " +
                "검증 대기 중");
        }

        /*
         * 11. 모든 조건을 충족한 경우에만 실행 가능
         */
        return new ReadinessEvaluation(
            "ready",
            true,
            "모든 실행 조건을 충족함");
    }

    private static async Task<
        SafeCommitStateResult>
        TransitionAsync(
            string safeCommitPath,
            string newStatus,
            bool canExecute,
            string reason,
            CancellationToken cancellationToken)
    {
        var document =
            await LoadDocumentAsync(
                safeCommitPath,
                cancellationToken);

        var currentStatus =
            GetRequiredString(
                document,
                "status");

        if (!IsTransitionAllowed(
                currentStatus,
                newStatus))
        {
            throw new InvalidOperationException(
                "Safe Commit 상태를 " +
                $"{currentStatus}에서 " +
                $"{newStatus}(으)로 변경할 수 없습니다.");
        }

        var now =
            DateTimeOffset.UtcNow;

        document["schemaVersion"] =
            Math.Max(
                GetInteger(
                    document,
                    "schemaVersion"),
                10);

        document["status"] =
            newStatus;

        document["canExecute"] =
            canExecute;

        document["updatedAtUtc"] =
            now.ToString("O");

        var history =
            GetOrCreateStateHistory(
                document);

        history.Add(
            new JsonObject
            {
                ["from"] =
                    currentStatus,

                ["to"] =
                    newStatus,

                ["atUtc"] =
                    now.ToString("O"),

                ["reason"] =
                    reason
            });

        await WriteDocumentAtomicallyAsync(
            safeCommitPath,
            document,
            cancellationToken);

        return new SafeCommitStateResult(
            CommitId:
                GetRequiredString(
                    document,
                    "commitId"),

            PreviousStatus:
                currentStatus,

            CurrentStatus:
                newStatus,

            CanExecute:
                canExecute,

            Reason:
                reason,

            FilePath:
                safeCommitPath);
    }

    private static bool IsTransitionAllowed(
        string currentStatus,
        string newStatus)
    {
        return AllowedTransitions.TryGetValue(
                   currentStatus,
                   out var allowedStatuses)
               &&
               allowedStatuses.Contains(
                   newStatus);
    }

    private static JsonArray
        GetOrCreateStateHistory(
            JsonObject document)
    {
        if (document["stateHistory"] is
            JsonArray history)
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
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Safe Commit 파일을 찾을 수 없습니다.",
                path);
        }

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
            TryDeleteFile(
                temporaryPath);

            throw;
        }
    }

    private static void TryDeleteFile(
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

    private static SafeCommitStateResult
        CreateCurrentResult(
            JsonObject document,
            string filePath,
            string reason)
    {
        var currentStatus =
            GetRequiredString(
                document,
                "status");

        return new SafeCommitStateResult(
            CommitId:
                GetRequiredString(
                    document,
                    "commitId"),

            PreviousStatus:
                currentStatus,

            CurrentStatus:
                currentStatus,

            CanExecute:
                GetBoolean(
                    document,
                    "canExecute"),

            Reason:
                reason,

            FilePath:
                filePath);
    }

    private static string GetRequiredString(
        JsonObject document,
        string propertyName)
    {
        var value =
            GetOptionalString(
                document,
                propertyName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException(
                "Safe Commit의 " +
                $"{propertyName} 값이 없습니다.");
        }

        return value;
    }

    private static string? GetOptionalString(
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

    private static bool GetBoolean(
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

    private static int GetInteger(
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

    private sealed record ReadinessEvaluation(
        string TargetStatus,
        bool CanExecute,
        string Reason);
}

internal sealed record SafeCommitStateResult(
    string CommitId,
    string PreviousStatus,
    string CurrentStatus,
    bool CanExecute,
    string Reason,
    string FilePath)
{
    public string DisplayStatus =>
        CurrentStatus switch
        {
            "draft" =>
                "초안",

            "validating" =>
                "검증 중",

            "awaiting_icon_snapshot" =>
                "아이콘 좌표 저장 대기",

            "ready" =>
                "실행 준비 완료",

            "validation_failed" =>
                "검증 실패",

            "executing" =>
                "실행 중",

            "completed" =>
                "완료",

            "incomplete" =>
                "미완료",

            "execution_failed" =>
                "실행 실패",

            "rolling_back" =>
                "복구 중",

            "rolled_back" =>
                "복구 완료",

            "rollback_failed" =>
                "복구 실패",

            _ =>
                CurrentStatus
        };
}
