using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using System.Text.Unicode;


namespace Recon.App.Services;

internal sealed class SafeCommitService
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

    public async Task<SafeCommitWriteResult>
        CreateDraftAsync(
            string commitName,
            string sourceFolder,
            string destinationRoot,
            IReadOnlyList<SafeCommitPlanInput>
                planItems,
            MovePreflightResult preflightResult,
            int skippedFileCount,
            int scanErrorCount,
            CancellationToken cancellationToken)
    {
        ValidateArguments(
            commitName,
            sourceFolder,
            destinationRoot,
            planItems);

        var preflightByFileId =
            BuildPreflightLookup(
                planItems,
                preflightResult);

        var createdAtUtc =
            DateTimeOffset.UtcNow;

        var idSuffix =
            Guid.NewGuid()
                .ToString("N")[..8]
                .ToUpperInvariant();

        var commitId =
            $"SC-{createdAtUtc:yyyyMMdd-HHmmss}-" +
            idSuffix;

        var fileEntries = planItems
            .Select(planItem =>
            {
                var preflightItem =
                    preflightByFileId[
                        planItem.FileId];

                return CreateFileEntry(
                    planItem,
                    preflightItem);
            })
            .ToArray();

        var generatedFolders =
            preflightResult.Items
                .Where(item =>
                    item.Status ==
                    MovePreflightStatus.Ready)
                .Select(item =>
                    Path.GetDirectoryName(
                        item.Plan.PlannedPath))
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path)
                .ToArray();

        var document =
            new SafeCommitDocument(
                SchemaVersion: 6,
                CommitId: commitId,
                CommitName: commitName,
                CreatedAtUtc: createdAtUtc,
                Status: "draft",
                CanExecute: false,
                SourceFolder: sourceFolder,
                DestinationRoot: destinationRoot,

                PreflightValidated: true,
                PreflightValidatedAtUtc:
                    DateTimeOffset.UtcNow,

                ApprovedFileCount:
                    planItems.Count(item =>
                        item.IsApproved),

                ReadyFileCount:
                    preflightResult.ReadyCount,

                ExcludedFileCount:
                    preflightResult.ExcludedCount,

                BlockedFileCount:
                    preflightResult.BlockedCount,

                SkippedFileCount:
                    skippedFileCount,

                ScanErrorCount:
                    scanErrorCount,

                DesktopIconSnapshotCaptured:
                    false,

                DesktopIconSnapshotVerified:
                    false,

                Files:
                    fileEntries,

                GeneratedFolders:
                    generatedFolders);

        var commitDirectory =
            GetCommitDirectory();

        Directory.CreateDirectory(
            commitDirectory);

        var finalPath =
            Path.Combine(
                commitDirectory,
                $"{commitId}.json");

        var temporaryPath =
            Path.Combine(
                commitDirectory,
                $".{commitId}." +
                $"{Guid.NewGuid():N}.tmp");

        var json =
            JsonSerializer.Serialize(
                document,
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

            File.Move(
                temporaryPath,
                finalPath,
                overwrite: false);
        }
        catch
        {
            TryDeleteTemporaryFile(
                temporaryPath);

            throw;
        }

        if (!File.Exists(finalPath))
        {
            throw new IOException(
                "Safe Commit 저장을 확인할 수 없습니다.");
        }

        return new SafeCommitWriteResult(
            CommitId: commitId,
            FilePath: finalPath,
            ApprovedFileCount:
                document.ApprovedFileCount,
            ReadyFileCount:
                document.ReadyFileCount,
            ExcludedFileCount:
                document.ExcludedFileCount,
            BlockedFileCount:
                document.BlockedFileCount,
            CanExecute:
                document.CanExecute);
    }

    private static void ValidateArguments(
        string commitName,
        string sourceFolder,
        string destinationRoot,
        IReadOnlyList<SafeCommitPlanInput>
            planItems)
    {
        if (string.IsNullOrWhiteSpace(commitName))
        {
            throw new ArgumentException(
                "Safe Commit 이름이 없습니다.",
                nameof(commitName));
        }

        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            throw new ArgumentException(
                "원본 폴더가 없습니다.",
                nameof(sourceFolder));
        }

        if (string.IsNullOrWhiteSpace(
                destinationRoot))
        {
            throw new ArgumentException(
                "정리 목적지가 없습니다.",
                nameof(destinationRoot));
        }

        if (planItems.Count == 0)
        {
            throw new InvalidOperationException(
                "저장할 정리 계획이 없습니다.");
        }
    }

    private static IReadOnlyDictionary<
        string,
        MovePreflightItem>
        BuildPreflightLookup(
            IReadOnlyList<SafeCommitPlanInput>
                planItems,
            MovePreflightResult preflightResult)
    {
        if (planItems.Count !=
            preflightResult.Items.Count)
        {
            throw new InvalidOperationException(
                "정리 계획과 검증 결과의 " +
                "파일 개수가 일치하지 않습니다.");
        }

        var lookup =
            preflightResult.Items
                .GroupBy(
                    item => item.Plan.FileId,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);

        foreach (var planItem in planItems)
        {
            if (!lookup.TryGetValue(
                    planItem.FileId,
                    out var preflightItem))
            {
                throw new InvalidOperationException(
                    $"파일 {planItem.FileId}의 " +
                    "검증 결과가 없습니다.");
            }

            if (!string.Equals(
                    planItem.OriginalPath,
                    preflightItem.Plan.OriginalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"파일 {planItem.FileId}의 " +
                    "원본 경로가 검증 결과와 다릅니다.");
            }

            if (!string.Equals(
                    planItem.PlannedPath,
                    preflightItem.Plan.PlannedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"파일 {planItem.FileId}의 " +
                    "이동 예정 경로가 검증 결과와 다릅니다.");
            }
        }

        return lookup;
    }

    private static SafeCommitFileEntry
        CreateFileEntry(
            SafeCommitPlanInput planItem,
            MovePreflightItem preflightItem)
    {
        var preflightStatus =
            preflightItem.Status switch
            {
                MovePreflightStatus.Ready =>
                    "ready",

                MovePreflightStatus.Excluded =>
                    "excluded",

                _ => "blocked"
            };

        var fileStatus =
            preflightItem.Status switch
            {
                MovePreflightStatus.Ready =>
                    "planned",

                MovePreflightStatus.Excluded =>
                    "excluded",

                _ => "blocked"
            };

        return new SafeCommitFileEntry(
            FileId: planItem.FileId,
            FileName: planItem.FileName,
            OriginalPath:
                planItem.OriginalPath,
            PlannedPath:
                planItem.PlannedPath,
            Category:
                planItem.Category,
            Confidence:
                planItem.Confidence,
            Reason:
                planItem.Reason,
            MoveRisk:
                planItem.MoveRisk,
            Approved:
                planItem.IsApproved,
            Status:
                fileStatus,
            PreflightStatus:
                preflightStatus,
            PreflightReason:
                preflightItem.Reason,
            ScannedSizeBytes:
                planItem.ScannedSizeBytes,
            ScannedLastWriteTimeUtc:
                planItem.ScannedLastWriteTimeUtc,
            Hash:
                planItem.Sha256Hash,
            HashStatus:
                planItem.HashStatus,
            HashReason:
                planItem.HashReason);
    }

    private static string GetCommitDirectory()
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
            "commits");
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
            // 임시 파일 정리 실패는
            // 원래 저장 오류를 덮어쓰지 않습니다.
        }
    }
}

internal sealed record SafeCommitPlanInput(
    string FileId,
    string FileName,
    string OriginalPath,
    string PlannedPath,
    string Category,
    string Confidence,
    string Reason,
    string MoveRisk,
    long ScannedSizeBytes,
    DateTime ScannedLastWriteTimeUtc,
    string? Sha256Hash,
    string HashStatus,
    string? HashReason,
    bool IsApproved);

internal sealed record SafeCommitWriteResult(
    string CommitId,
    string FilePath,
    int ApprovedFileCount,
    int ReadyFileCount,
    int ExcludedFileCount,
    int BlockedFileCount,
    bool CanExecute);

internal sealed record SafeCommitDocument(
    int SchemaVersion,
    string CommitId,
    string CommitName,
    DateTimeOffset CreatedAtUtc,
    string Status,
    bool CanExecute,
    string SourceFolder,
    string DestinationRoot,

    bool PreflightValidated,
    DateTimeOffset PreflightValidatedAtUtc,

    int ApprovedFileCount,
    int ReadyFileCount,
    int ExcludedFileCount,
    int BlockedFileCount,
    int SkippedFileCount,
    int ScanErrorCount,

    bool DesktopIconSnapshotCaptured,
    bool DesktopIconSnapshotVerified,

    IReadOnlyList<SafeCommitFileEntry> Files,
    IReadOnlyList<string> GeneratedFolders);

internal sealed record SafeCommitFileEntry(
    string FileId,
    string FileName,
    string OriginalPath,
    string PlannedPath,
    string Category,
    string Confidence,
    string Reason,
    string MoveRisk,
    bool Approved,
    string Status,
    string PreflightStatus,
    string PreflightReason,
    long ScannedSizeBytes,
    DateTime ScannedLastWriteTimeUtc,
    string? Hash,
    string HashStatus,
    string? HashReason);
    