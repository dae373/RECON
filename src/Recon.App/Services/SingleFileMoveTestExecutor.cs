using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace Recon.App.Services;

internal sealed class SingleFileMoveTestExecutor
{
    private static readonly JsonSerializerOptions
        JsonOptions = new()
        {
            WriteIndented = true,

            Encoder =
                JavaScriptEncoder.Create(
                    UnicodeRanges.All)
        };

    public async Task<SingleFileMoveTestResult>
        ExecuteAsync(
            string safeCommitPath,
            CancellationToken cancellationToken)
    {
        if (Forms.Screen.AllScreens.Length != 1)
        {
            return Failure(
                "단일 파일 이동 테스트는 " +
                "모니터 1대 환경에서만 실행할 수 있습니다.");
        }

        if (!File.Exists(safeCommitPath))
        {
            return Failure(
                "Safe Commit 파일을 찾을 수 없습니다.");
        }

        JsonObject safeCommit;

        try
        {
            safeCommit =
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

        var commitId =
            ReadString(
                safeCommit,
                "commitId");

        var safeCommitStatus =
            ReadString(
                safeCommit,
                "status");

        var canExecute =
            ReadBoolean(
                safeCommit,
                "canExecute");

        var finalDryRunPassed =
            ReadBoolean(
                safeCommit,
                "finalDryRunPassed");

        var sourceFolder =
            ReadString(
                safeCommit,
                "sourceFolder");

        var destinationRoot =
            ReadString(
                safeCommit,
                "destinationRoot");

        if (string.IsNullOrWhiteSpace(commitId)
            ||
            string.IsNullOrWhiteSpace(safeCommitStatus)
            ||
            string.IsNullOrWhiteSpace(sourceFolder)
            ||
            string.IsNullOrWhiteSpace(destinationRoot))
        {
            return Failure(
                "Safe Commit 필수 정보가 없습니다.");
        }

        if (!string.Equals(
                safeCommitStatus,
                "ready",
                StringComparison.OrdinalIgnoreCase)
            ||
            !canExecute
            ||
            !finalDryRunPassed)
        {
            return Failure(
                "실행 준비와 최종 드라이런을 " +
                "통과한 Safe Commit이 아닙니다.");
        }

        if (!IsReconTestEnvironment(
                sourceFolder))
        {
            return Failure(
                "파일 이동 테스트는 RECON_TEST_ENV " +
                "폴더에서만 실행할 수 있습니다.");
        }

        if (IsCloudSyncedPath(
                destinationRoot))
        {
            return Failure(
                "클라우드 동기화 목적지로는 " +
                "파일을 이동할 수 없습니다.");
        }

        PreparedJournal? preparedJournal;

        try
        {
            preparedJournal =
                await FindLatestPreparedJournalAsync(
                    commitId,
                    cancellationToken);
        }
        catch (Exception exception)
        {
            return Failure(
                "준비된 작업 저널을 읽지 못했습니다: " +
                exception.Message);
        }

        if (preparedJournal is null)
        {
            return Failure(
                "이 Safe Commit에 연결된 prepared " +
                "작업 저널이 없습니다.");
        }

        var journal =
            preparedJournal.Document;

        if (!string.Equals(
                ReadString(journal, "mode"),
                "single_file_test",
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "단일 파일 테스트용 작업 저널이 아닙니다.");
        }

        if (ReadInteger(
                journal,
                "operationLimit") != 1)
        {
            return Failure(
                "작업 저널의 파일 제한이 1개가 아닙니다.");
        }

        if (journal["operations"] is not
            JsonArray operations
            ||
            operations.Count != 1
            ||
            operations[0] is not
                JsonObject operation)
        {
            return Failure(
                "작업 저널의 이동 작업이 정확히 " +
                "1개가 아닙니다.");
        }

        var fileId =
            ReadString(
                operation,
                "fileId");

        var fileName =
            ReadString(
                operation,
                "fileName");

        var originalPath =
            ReadString(
                operation,
                "originalPath");

        var plannedPath =
            ReadString(
                operation,
                "plannedPath");

        var expectedHash =
            ReadString(
                operation,
                "expectedHash");

        if (string.IsNullOrWhiteSpace(fileId)
            ||
            string.IsNullOrWhiteSpace(fileName)
            ||
            string.IsNullOrWhiteSpace(originalPath)
            ||
            string.IsNullOrWhiteSpace(plannedPath)
            ||
            string.IsNullOrWhiteSpace(expectedHash))
        {
            return Failure(
                "작업 저널의 파일 정보가 부족합니다.");
        }

        if (safeCommit["files"] is not
            JsonArray safeCommitFiles)
        {
            return Failure(
                "Safe Commit 파일 목록이 없습니다.");
        }

        var safeCommitFile =
            safeCommitFiles
                .OfType<JsonObject>()
                .FirstOrDefault(file =>
                    string.Equals(
                        ReadString(file, "fileId"),
                        fileId,
                        StringComparison.OrdinalIgnoreCase));

        if (safeCommitFile is null)
        {
            return Failure(
                "작업 저널의 파일을 Safe Commit에서 " +
                "찾을 수 없습니다.");
        }

        var validation =
            await ValidateImmediatelyBeforeMoveAsync(
                safeCommitFile,
                originalPath,
                plannedPath,
                expectedHash,
                sourceFolder,
                destinationRoot,
                cancellationToken);

        if (!validation.IsSuccessful)
        {
            await MarkJournalFailedAsync(
                preparedJournal.Path,
                journal,
                operation,
                validation.Reason,
                cancellationToken);

            return Failure(
                validation.Reason,
                preparedJournal.Path,
                fileId,
                fileName,
                originalPath,
                plannedPath);
        }

        var destinationDirectory =
            Path.GetDirectoryName(
                plannedPath);

        if (string.IsNullOrWhiteSpace(
                destinationDirectory))
        {
            return Failure(
                "목적지 폴더 경로가 없습니다.");
        }

        var sourceDrive =
            Path.GetPathRoot(
                originalPath);

        var destinationDrive =
            Path.GetPathRoot(
                plannedPath);

        if (!string.Equals(
                sourceDrive,
                destinationDrive,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "단일 파일 테스트는 같은 드라이브 " +
                "안에서만 실행할 수 있습니다.");
        }

        var startedAtUtc =
            DateTimeOffset.UtcNow;

        /*
         * 작업 저널을 먼저 executing 상태로 저장합니다.
         */
        journal["status"] =
            "executing";

        journal["updatedAtUtc"] =
            startedAtUtc.ToString("O");

        operation["status"] =
            "moving";

        operation["startedAtUtc"] =
            startedAtUtc.ToString("O");

        operation["error"] =
            null;

        await WriteDocumentAtomicallyAsync(
            preparedJournal.Path,
            journal,
            cancellationToken);

        /*
         * Safe Commit도 executing 상태로 저장합니다.
         */
        AddStateHistory(
            safeCommit,
            safeCommitStatus,
            "executing",
            "단일 파일 이동 테스트 시작");

        safeCommit["status"] =
            "executing";

        safeCommit["canExecute"] =
            false;

        safeCommit["updatedAtUtc"] =
            startedAtUtc.ToString("O");

        await WriteDocumentAtomicallyAsync(
            safeCommitPath,
            safeCommit,
            cancellationToken);

        var fileWasMoved = false;
        var rollbackSucceeded = false;

        try
        {
            Directory.CreateDirectory(
                destinationDirectory);

            /*
             * overwrite:false이므로 동일 이름 파일이
             * 존재하면 예외가 발생합니다.
             */
            File.Move(
                originalPath,
                plannedPath,
                overwrite: false);

            fileWasMoved = true;

            var actualHash =
                await ComputeHashAsync(
                    plannedPath,
                    cancellationToken);

            if (!string.Equals(
                    actualHash,
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "이동 후 SHA-256 해시가 " +
                    "Safe Commit과 일치하지 않습니다.");
            }

            var completedAtUtc =
                DateTimeOffset.UtcNow;

            /*
             * 작업 저널 완료 기록
             */
            journal["status"] =
                "completed";

            journal["updatedAtUtc"] =
                completedAtUtc.ToString("O");

            operation["status"] =
                "verified";

            operation["actualPath"] =
                plannedPath;

            operation["actualHash"] =
                actualHash;

            operation["completedAtUtc"] =
                completedAtUtc.ToString("O");

            operation["error"] =
                null;

            await WriteDocumentAtomicallyAsync(
                preparedJournal.Path,
                journal,
                cancellationToken);

            /*
             * Safe Commit의 해당 파일 결과 갱신
             */
            safeCommitFile["executionStatus"] =
                "verified";

            safeCommitFile["currentPath"] =
                plannedPath;

            safeCommitFile["actualHash"] =
                actualHash;

            safeCommitFile["movedAtUtc"] =
                completedAtUtc.ToString("O");

            safeCommit["executionMode"] =
                "single_file_test";

            safeCommit["executedFileCount"] =
                1;

            safeCommit["lastOperationJournalPath"] =
                preparedJournal.Path;

            /*
             * 전체 파일을 이동한 것이 아니므로
             * Safe Commit 전체 상태는 incomplete입니다.
             */
            AddStateHistory(
                safeCommit,
                "executing",
                "incomplete",
                "단일 파일 이동 테스트 완료");

            safeCommit["status"] =
                "incomplete";

            safeCommit["canExecute"] =
                false;

            safeCommit["updatedAtUtc"] =
                completedAtUtc.ToString("O");

            await WriteDocumentAtomicallyAsync(
                safeCommitPath,
                safeCommit,
                cancellationToken);

            return new SingleFileMoveTestResult(
                IsSuccessful: true,
                MoveSucceeded: true,
                VerificationSucceeded: true,
                RollbackAttempted: false,
                RollbackSucceeded: false,
                Reason:
                    "파일 1개를 이동하고 SHA-256 " +
                    "검증을 완료했습니다.",
                JournalPath:
                    preparedJournal.Path,
                FileId:
                    fileId,
                FileName:
                    fileName,
                OriginalPath:
                    originalPath,
                CurrentPath:
                    plannedPath,
                ExpectedHash:
                    expectedHash,
                ActualHash:
                    actualHash);
        }
        catch (OperationCanceledException)
        {
            if (fileWasMoved)
            {
                rollbackSucceeded =
                    TryRollback(
                        plannedPath,
                        originalPath);
            }

            await MarkExecutionFailureAsync(
                safeCommitPath,
                safeCommit,
                preparedJournal.Path,
                journal,
                operation,
                "작업이 취소됨",
                rollbackSucceeded,
                CancellationToken.None);

            throw;
        }
        catch (Exception exception)
        {
            if (fileWasMoved)
            {
                rollbackSucceeded =
                    TryRollback(
                        plannedPath,
                        originalPath);
            }

            await MarkExecutionFailureAsync(
                safeCommitPath,
                safeCommit,
                preparedJournal.Path,
                journal,
                operation,
                exception.Message,
                rollbackSucceeded,
                CancellationToken.None);

            return new SingleFileMoveTestResult(
                IsSuccessful: false,
                MoveSucceeded: fileWasMoved,
                VerificationSucceeded: false,
                RollbackAttempted: fileWasMoved,
                RollbackSucceeded: rollbackSucceeded,
                Reason:
                    "단일 파일 이동 테스트 실패: " +
                    exception.Message,
                JournalPath:
                    preparedJournal.Path,
                FileId:
                    fileId,
                FileName:
                    fileName,
                OriginalPath:
                    originalPath,
                CurrentPath:
                    rollbackSucceeded
                        ? originalPath
                        : plannedPath,
                ExpectedHash:
                    expectedHash,
                ActualHash:
                    null);
        }
    }

    private static async Task<MoveValidationResult>
        ValidateImmediatelyBeforeMoveAsync(
            JsonObject safeCommitFile,
            string originalPath,
            string plannedPath,
            string expectedHash,
            string sourceFolder,
            string destinationRoot,
            CancellationToken cancellationToken)
    {
        if (!ReadBoolean(
                safeCommitFile,
                "approved"))
        {
            return MoveValidationResult.Failure(
                "사용자가 승인하지 않은 파일입니다.");
        }

        if (!string.Equals(
                ReadString(
                    safeCommitFile,
                    "preflightStatus"),
                "ready",
                StringComparison.OrdinalIgnoreCase))
        {
            return MoveValidationResult.Failure(
                "이동 전 검증을 통과하지 못한 파일입니다.");
        }

        if (!string.Equals(
                ReadString(
                    safeCommitFile,
                    "dryRunStatus"),
                "ready",
                StringComparison.OrdinalIgnoreCase))
        {
            return MoveValidationResult.Failure(
                "최종 드라이런을 통과하지 못한 파일입니다.");
        }

        string normalizedSource;
        string normalizedDestination;
        string normalizedSourceRoot;
        string normalizedDestinationRoot;

        try
        {
            normalizedSource =
                Path.GetFullPath(originalPath);

            normalizedDestination =
                Path.GetFullPath(plannedPath);

            normalizedSourceRoot =
                NormalizeDirectoryPath(
                    sourceFolder);

            normalizedDestinationRoot =
                NormalizeDirectoryPath(
                    destinationRoot);
        }
        catch
        {
            return MoveValidationResult.Failure(
                "파일 경로 형식이 잘못됐습니다.");
        }

        if (!normalizedSource.StartsWith(
                normalizedSourceRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return MoveValidationResult.Failure(
                "원본 파일이 허용된 테스트 폴더 " +
                "외부에 있습니다.");
        }

        if (!normalizedDestination.StartsWith(
                normalizedDestinationRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return MoveValidationResult.Failure(
                "목적지가 RECON 정리함 " +
                "외부에 있습니다.");
        }

        if (!File.Exists(normalizedSource))
        {
            return MoveValidationResult.Failure(
                "원본 파일이 존재하지 않습니다.");
        }

        if (File.Exists(normalizedDestination)
            ||
            Directory.Exists(normalizedDestination))
        {
            return MoveValidationResult.Failure(
                "목적지에 같은 이름의 항목이 " +
                "이미 존재합니다.");
        }

        var fileInfo =
            new FileInfo(normalizedSource);

        fileInfo.Refresh();

        var scannedSize =
            ReadLong(
                safeCommitFile,
                "scannedSizeBytes");

        var scannedLastWriteTime =
            ReadDateTime(
                safeCommitFile,
                "scannedLastWriteTimeUtc");

        if (fileInfo.Length != scannedSize)
        {
            return MoveValidationResult.Failure(
                "스캔 후 파일 크기가 변경됐습니다.");
        }

        if (scannedLastWriteTime is null
            ||
            fileInfo.LastWriteTimeUtc !=
            scannedLastWriteTime.Value)
        {
            return MoveValidationResult.Failure(
                "스캔 후 파일 수정 시간이 변경됐습니다.");
        }

        string currentHash;

        try
        {
            currentHash =
                await ComputeHashAsync(
                    normalizedSource,
                    cancellationToken);
        }
        catch (Exception exception)
            when (exception is
                IOException
                or UnauthorizedAccessException
                or CryptographicException
                or NotSupportedException)
        {
            return MoveValidationResult.Failure(
                "원본 파일 해시를 계산하지 " +
                $"못했습니다: {exception.Message}");
        }

        if (!string.Equals(
                currentHash,
                expectedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            return MoveValidationResult.Failure(
                "원본 파일 해시가 Safe Commit과 " +
                "일치하지 않습니다.");
        }

        return MoveValidationResult.Success();
    }

    private static async Task<string>
        ComputeHashAsync(
            string path,
            CancellationToken cancellationToken)
    {
        await using var stream =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 131072,
                options:
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);

        var hash =
            await SHA256.HashDataAsync(
                stream,
                cancellationToken);

        return Convert.ToHexString(hash)
            .ToLowerInvariant();
    }

    private static async Task<PreparedJournal?>
        FindLatestPreparedJournalAsync(
            string safeCommitId,
            CancellationToken cancellationToken)
    {
        var journalDirectory =
            GetJournalDirectory();

        if (!Directory.Exists(journalDirectory))
        {
            return null;
        }

        var journalPaths =
            Directory
                .EnumerateFiles(
                    journalDirectory,
                    "*.json",
                    SearchOption.TopDirectoryOnly)
                .OrderByDescending(path =>
                    File.GetLastWriteTimeUtc(path))
                .ToArray();

        foreach (var path in journalPaths)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            try
            {
                var document =
                    await LoadDocumentAsync(
                        path,
                        cancellationToken);

                if (string.Equals(
                        ReadString(
                            document,
                            "safeCommitId"),
                        safeCommitId,
                        StringComparison.OrdinalIgnoreCase)
                    &&
                    string.Equals(
                        ReadString(
                            document,
                            "status"),
                        "prepared",
                        StringComparison.OrdinalIgnoreCase)
                    &&
                    string.Equals(
                        ReadString(
                            document,
                            "mode"),
                        "single_file_test",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new PreparedJournal(
                        path,
                        document);
                }
            }
            catch
            {
                // 읽을 수 없는 저널은 건너뜁니다.
            }
        }

        return null;
    }

    private static async Task MarkJournalFailedAsync(
        string journalPath,
        JsonObject journal,
        JsonObject operation,
        string reason,
        CancellationToken cancellationToken)
    {
        var now =
            DateTimeOffset.UtcNow;

        journal["status"] =
            "validation_failed";

        journal["updatedAtUtc"] =
            now.ToString("O");

        operation["status"] =
            "blocked";

        operation["completedAtUtc"] =
            now.ToString("O");

        operation["error"] =
            reason;

        await WriteDocumentAtomicallyAsync(
            journalPath,
            journal,
            cancellationToken);
    }

    private static async Task
        MarkExecutionFailureAsync(
            string safeCommitPath,
            JsonObject safeCommit,
            string journalPath,
            JsonObject journal,
            JsonObject operation,
            string reason,
            bool rollbackSucceeded,
            CancellationToken cancellationToken)
    {
        var now =
            DateTimeOffset.UtcNow;

        journal["status"] =
            rollbackSucceeded
                ? "rolled_back_after_failure"
                : "execution_failed";

        journal["updatedAtUtc"] =
            now.ToString("O");

        operation["status"] =
            rollbackSucceeded
                ? "rolled_back"
                : "failed";

        operation["completedAtUtc"] =
            now.ToString("O");

        operation["error"] =
            reason;

        await WriteDocumentAtomicallyAsync(
            journalPath,
            journal,
            cancellationToken);

        var previousStatus =
            ReadString(
                safeCommit,
                "status")
            ?? "executing";

        var nextStatus =
            rollbackSucceeded
                ? "incomplete"
                : "execution_failed";

        AddStateHistory(
            safeCommit,
            previousStatus,
            nextStatus,
            rollbackSucceeded
                ? "이동 실패 후 원래 위치 복구"
                : "이동 테스트 실패");

        safeCommit["status"] =
            nextStatus;

        safeCommit["canExecute"] =
            false;

        safeCommit["updatedAtUtc"] =
            now.ToString("O");

        safeCommit["lastExecutionError"] =
            reason;

        safeCommit["lastOperationJournalPath"] =
            journalPath;

        await WriteDocumentAtomicallyAsync(
            safeCommitPath,
            safeCommit,
            cancellationToken);
    }

    private static bool TryRollback(
        string currentPath,
        string originalPath)
    {
        try
        {
            if (!File.Exists(currentPath))
            {
                return false;
            }

            if (File.Exists(originalPath)
                ||
                Directory.Exists(originalPath))
            {
                return false;
            }

            var originalDirectory =
                Path.GetDirectoryName(
                    originalPath);

            if (!string.IsNullOrWhiteSpace(
                    originalDirectory))
            {
                Directory.CreateDirectory(
                    originalDirectory);
            }

            File.Move(
                currentPath,
                originalPath,
                overwrite: false);

            return File.Exists(originalPath)
                   &&
                   !File.Exists(currentPath);
        }
        catch
        {
            return false;
        }
    }

    private static void AddStateHistory(
        JsonObject document,
        string from,
        string to,
        string reason)
    {
        JsonArray history;

        if (document["stateHistory"] is
            JsonArray existingHistory)
        {
            history =
                existingHistory;
        }
        else
        {
            history =
                new JsonArray();

            document["stateHistory"] =
                history;
        }

        history.Add(
            new JsonObject
            {
                ["from"] =
                    from,

                ["to"] =
                    to,

                ["atUtc"] =
                    DateTimeOffset.UtcNow
                        .ToString("O"),

                ["reason"] =
                    reason
            });
    }

    private static bool IsReconTestEnvironment(
        string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .Split(
                    [
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    ],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(part =>
                    string.Equals(
                        part,
                        "RECON_TEST_ENV",
                        StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCloudSyncedPath(
        string path)
    {
        string normalizedPath;

        try
        {
            normalizedPath =
                NormalizeDirectoryPath(path);
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

            try
            {
                var normalizedCloudRoot =
                    NormalizeDirectoryPath(
                        cloudRoot);

                if (normalizedPath.StartsWith(
                        normalizedCloudRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // 잘못된 환경 변수는 무시합니다.
            }
        }

        return false;
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

    private static string GetJournalDirectory()
    {
        var localAppData =
            Environment.GetFolderPath(
                Environment.SpecialFolder
                    .LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "로컬 애플리케이션 데이터 " +
                "폴더를 찾을 수 없습니다.");
        }

        return Path.Combine(
            localAppData,
            "RECON",
            "operations");
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
                   "JSON 형식이 올바르지 않습니다.");
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
                "JSON 저장 폴더가 없습니다.");
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
            TryDelete(
                temporaryPath);

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

    private static SingleFileMoveTestResult
        Failure(
            string reason,
            string? journalPath = null,
            string? fileId = null,
            string? fileName = null,
            string? originalPath = null,
            string? currentPath = null)
    {
        return new SingleFileMoveTestResult(
            IsSuccessful: false,
            MoveSucceeded: false,
            VerificationSucceeded: false,
            RollbackAttempted: false,
            RollbackSucceeded: false,
            Reason: reason,
            JournalPath: journalPath,
            FileId: fileId,
            FileName: fileName,
            OriginalPath: originalPath,
            CurrentPath: currentPath,
            ExpectedHash: null,
            ActualHash: null);
    }

    private sealed record PreparedJournal(
        string Path,
        JsonObject Document);

    private sealed record MoveValidationResult(
        bool IsSuccessful,
        string Reason)
    {
        public static MoveValidationResult Success()
        {
            return new MoveValidationResult(
                true,
                "이동 직전 검증 통과");
        }

        public static MoveValidationResult Failure(
            string reason)
        {
            return new MoveValidationResult(
                false,
                reason);
        }
    }
}

internal sealed record SingleFileMoveTestResult(
    bool IsSuccessful,
    bool MoveSucceeded,
    bool VerificationSucceeded,
    bool RollbackAttempted,
    bool RollbackSucceeded,
    string Reason,
    string? JournalPath,
    string? FileId,
    string? FileName,
    string? OriginalPath,
    string? CurrentPath,
    string? ExpectedHash,
    string? ActualHash);
