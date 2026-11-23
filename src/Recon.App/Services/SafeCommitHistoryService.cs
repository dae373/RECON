using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Recon.App.Services;

internal sealed class SafeCommitHistoryService
{
    private static readonly JsonSerializerOptions
        JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

    public string StorageDirectory
    {
        get
        {
            var localAppData =
                Environment.GetFolderPath(
                    Environment.SpecialFolder
                        .LocalApplicationData);

            return Path.Combine(
                localAppData,
                "RECON",
                "commits");
        }
    }

    public async Task<
        IReadOnlyList<SafeCommitHistoryEntry>>
        LoadAsync(
            CancellationToken cancellationToken)
    {
        if (!Directory.Exists(StorageDirectory))
        {
            return [];
        }

        string[] jsonFiles;

        try
        {
            jsonFiles = Directory
                .EnumerateFiles(
                    StorageDirectory,
                    "*.json",
                    SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (Exception exception)
            when (IsExpectedException(exception))
        {
            throw new IOException(
                "Safe Commit 목록을 읽지 못했습니다.",
                exception);
        }

        var entries =
            new List<SafeCommitHistoryEntry>();

        foreach (var filePath in jsonFiles)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            try
            {
                var json =
                    await File.ReadAllTextAsync(
                        filePath,
                        cancellationToken);

                var document =
                    JsonSerializer.Deserialize<
                        HistoryDocument>(
                        json,
                        JsonOptions);

                if (document is null)
                {
                    throw new JsonException(
                        "JSON 문서가 비어 있습니다.");
                }

                var commitId =
                    string.IsNullOrWhiteSpace(
                        document.CommitId)
                        ? Path.GetFileNameWithoutExtension(
                            filePath)
                        : document.CommitId;

                var commitName =
                    string.IsNullOrWhiteSpace(
                        document.CommitName)
                        ? "이름 없는 Safe Commit"
                        : document.CommitName;

                entries.Add(
                    new SafeCommitHistoryEntry(
                        CommitId: commitId,
                        CommitName: commitName,
                        CreatedAtUtc:
                            document.CreatedAtUtc,
                        Status:
                            document.Status ?? "unknown",
                        CanExecute:
                            document.CanExecute,
                        SourceFolder:
                            document.SourceFolder ?? "",
                        DestinationRoot:
                            document.DestinationRoot ?? "",
                        ApprovedFileCount:
                            document.ApprovedFileCount,
                        ExcludedFileCount:
                            document.ExcludedFileCount,
                        SkippedFileCount:
                            document.SkippedFileCount,
                        ScanErrorCount:
                            document.ScanErrorCount,
                        FilePath:
                            filePath,
                        HasLoadError: false,
                        LoadError: null));
            }
            catch (Exception exception)
                when (IsExpectedException(exception))
            {
                entries.Add(
                    new SafeCommitHistoryEntry(
                        CommitId:
                            Path.GetFileNameWithoutExtension(
                                filePath),
                        CommitName:
                            "읽기 실패",
                        CreatedAtUtc:
                            GetLastWriteTimeSafely(
                                filePath),
                        Status:
                            "corrupted",
                        CanExecute:
                            false,
                        SourceFolder:
                            "",
                        DestinationRoot:
                            "",
                        ApprovedFileCount:
                            0,
                        ExcludedFileCount:
                            0,
                        SkippedFileCount:
                            0,
                        ScanErrorCount:
                            1,
                        FilePath:
                            filePath,
                        HasLoadError:
                            true,
                        LoadError:
                            exception.Message));
            }
        }

        return entries
            .OrderByDescending(entry =>
                entry.CreatedAtUtc)
            .ToArray();
    }

    private static DateTimeOffset
        GetLastWriteTimeSafely(
            string filePath)
    {
        try
        {
            return File.GetLastWriteTimeUtc(
                filePath);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static bool IsExpectedException(
        Exception exception)
    {
        return exception is
            IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException;
    }

    private sealed class HistoryDocument
    {
        public int SchemaVersion { get; init; }

        public string? CommitId { get; init; }

        public string? CommitName { get; init; }

        public DateTimeOffset CreatedAtUtc
        {
            get;
            init;
        }

        public string? Status { get; init; }

        public bool CanExecute { get; init; }

        public string? SourceFolder { get; init; }

        public string? DestinationRoot
        {
            get;
            init;
        }

        public int ApprovedFileCount
        {
            get;
            init;
        }

        public int ExcludedFileCount
        {
            get;
            init;
        }

        public int SkippedFileCount
        {
            get;
            init;
        }

        public int ScanErrorCount
        {
            get;
            init;
        }
    }
}

internal sealed record SafeCommitHistoryEntry(
    string CommitId,
    string CommitName,
    DateTimeOffset CreatedAtUtc,
    string Status,
    bool CanExecute,
    string SourceFolder,
    string DestinationRoot,
    int ApprovedFileCount,
    int ExcludedFileCount,
    int SkippedFileCount,
    int ScanErrorCount,
    string FilePath,
    bool HasLoadError,
    string? LoadError);
