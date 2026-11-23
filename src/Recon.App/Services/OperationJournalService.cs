using System;
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

internal sealed class OperationJournalService
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
        OperationJournalPreparationResult>
        PrepareSingleFileTestAsync(
            string safeCommitPath,
            CancellationToken cancellationToken)
    {
        if (!File.Exists(safeCommitPath))
        {
            return Failure(
                "Safe Commit 파일을 찾을 수 없습니다.");
        }

        try
        {
            var safeCommit =
                await LoadDocumentAsync(
                    safeCommitPath,
                    cancellationToken);

            var status =
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

            if (!string.Equals(
                    status,
                    "ready",
                    StringComparison.OrdinalIgnoreCase)
                ||
                !canExecute
                ||
                !finalDryRunPassed)
            {
                return Failure(
                    "실행 준비와 최종 드라이런을 " +
                    "모두 통과한 Safe Commit만 " +
                    "작업 저널을 만들 수 있습니다.");
            }

            var commitId =
                ReadString(
                    safeCommit,
                    "commitId");

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
                string.IsNullOrWhiteSpace(sourceFolder)
                ||
                string.IsNullOrWhiteSpace(destinationRoot))
            {
                return Failure(
                    "Safe Commit의 필수 경로 또는 " +
                    "ID가 없습니다.");
            }

            /*
             * 중요:
             * 실제 중요 폴더에서는 단일 파일 이동 테스트를
             * 실행하지 못하도록 제한합니다.
             */
            if (!IsReconTestEnvironment(
                    sourceFolder))
            {
                return Failure(
                    "단일 파일 이동 테스트는 " +
                    "RECON_TEST_ENV 폴더에서만 " +
                    "준비할 수 있습니다.");
            }

            if (safeCommit["files"] is not
                JsonArray files)
            {
                return Failure(
                    "Safe Commit 파일 목록이 없습니다.");
            }

            var selectedFile =
                files
                    .OfType<JsonObject>()
                    .Where(IsReadyForExecution)
                    .OrderBy(file =>
                        ReadString(
                            file,
                            "fileName"))
                    .FirstOrDefault();

            if (selectedFile is null)
            {
                return Failure(
                    "실행 가능한 테스트 파일이 없습니다.");
            }

            var fileId =
                ReadString(
                    selectedFile,
                    "fileId");

            var fileName =
                ReadString(
                    selectedFile,
                    "fileName");

            var originalPath =
                ReadString(
                    selectedFile,
                    "originalPath");

            var plannedPath =
                ReadString(
                    selectedFile,
                    "plannedPath");

            var expectedHash =
                ReadString(
                    selectedFile,
                    "hash");

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
                    "이동 대상 파일의 필수 정보가 " +
                    "부족합니다.");
            }

            if (!File.Exists(originalPath))
            {
                return Failure(
                    "이동 대상 원본 파일이 존재하지 " +
                    "않습니다.");
            }

            if (File.Exists(plannedPath)
                ||
                Directory.Exists(plannedPath))
            {
                return Failure(
                    "이동 예정 위치에 같은 이름의 " +
                    "항목이 이미 존재합니다.");
            }

            var createdAtUtc =
                DateTimeOffset.UtcNow;

            var journalId =
                $"OP-{createdAtUtc:yyyyMMdd-HHmmss}-" +
                $"{Guid.NewGuid()
                    .ToString("N")[..8]
                    .ToUpperInvariant()}";

            var operationId =
                $"MOVE-{Guid.NewGuid()
                    .ToString("N")[..8]
                    .ToUpperInvariant()}";

            var journal =
                new JsonObject
                {
                    ["schemaVersion"] =
                        1,

                    ["journalId"] =
                        journalId,

                    ["safeCommitId"] =
                        commitId,

                    ["safeCommitPath"] =
                        safeCommitPath,

                    ["mode"] =
                        "single_file_test",

                    ["status"] =
                        "prepared",

                    ["createdAtUtc"] =
                        createdAtUtc.ToString("O"),

                    ["updatedAtUtc"] =
                        createdAtUtc.ToString("O"),

                    ["sourceFolder"] =
                        sourceFolder,

                    ["destinationRoot"] =
                        destinationRoot,

                    ["operationLimit"] =
                        1,

                    ["operations"] =
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["operationId"] =
                                    operationId,

                                ["fileId"] =
                                    fileId,

                                ["fileName"] =
                                    fileName,

                                ["originalPath"] =
                                    originalPath,

                                ["plannedPath"] =
                                    plannedPath,

                                ["expectedHash"] =
                                    expectedHash,

                                ["status"] =
                                    "pending",

                                ["preparedAtUtc"] =
                                    createdAtUtc.ToString("O"),

                                ["startedAtUtc"] =
                                    null,

                                ["completedAtUtc"] =
                                    null,

                                ["error"] =
                                    null
                            }
                        }
                };

            var journalDirectory =
                GetJournalDirectory();

            Directory.CreateDirectory(
                journalDirectory);

            var journalPath =
                Path.Combine(
                    journalDirectory,
                    $"{journalId}.json");

            var temporaryPath =
                Path.Combine(
                    journalDirectory,
                    $".{journalId}." +
                    $"{Guid.NewGuid():N}.tmp");

            var json =
                journal.ToJsonString(
                    JsonOptions);

            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
                    new UTF8Encoding(false),
                    cancellationToken);

                File.Move(
                    temporaryPath,
                    journalPath,
                    overwrite: false);
            }
            catch
            {
                TryDelete(
                    temporaryPath);

                throw;
            }

            return new
                OperationJournalPreparationResult(
                    IsSuccessful: true,
                    JournalId: journalId,
                    JournalPath: journalPath,
                    SafeCommitId: commitId,
                    FileId: fileId,
                    FileName: fileName,
                    OriginalPath: originalPath,
                    PlannedPath: plannedPath,
                    Reason:
                        "단일 파일 이동 테스트를 위한 " +
                        "작업 저널을 준비했습니다.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                "작업 저널 준비 실패: " +
                exception.Message);
        }
    }

    private static bool IsReadyForExecution(
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
                   StringComparison.OrdinalIgnoreCase)
               &&
               string.Equals(
                   ReadString(
                       file,
                       "hashStatus"),
                   "computed",
                   StringComparison.OrdinalIgnoreCase)
               &&
               string.Equals(
                   ReadString(
                       file,
                       "dryRunStatus"),
                   "ready",
                   StringComparison.OrdinalIgnoreCase)
               &&
               !string.IsNullOrWhiteSpace(
                   ReadString(
                       file,
                       "hash"));
    }

    private static bool IsReconTestEnvironment(
        string sourceFolder)
    {
        try
        {
            var fullPath =
                Path.GetFullPath(sourceFolder);

            var parts =
                fullPath.Split(
                    [
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    ],
                    StringSplitOptions.RemoveEmptyEntries);

            return parts.Any(part =>
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
                   "Safe Commit JSON 형식이 " +
                   "올바르지 않습니다.");
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
        OperationJournalPreparationResult
        Failure(
            string reason)
    {
        return new
            OperationJournalPreparationResult(
                IsSuccessful: false,
                JournalId: null,
                JournalPath: null,
                SafeCommitId: null,
                FileId: null,
                FileName: null,
                OriginalPath: null,
                PlannedPath: null,
                Reason: reason);
    }
}

internal sealed record
    OperationJournalPreparationResult(
        bool IsSuccessful,
        string? JournalId,
        string? JournalPath,
        string? SafeCommitId,
        string? FileId,
        string? FileName,
        string? OriginalPath,
        string? PlannedPath,
        string Reason);
