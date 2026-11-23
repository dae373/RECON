using System.Security;
using System.IO;

namespace Recon.App.Services;

internal sealed class FileScanService
{
    private static readonly HashSet<string>
        ExcludedExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".lnk",
                ".exe",
                ".dll",
                ".sys",
                ".com",
                ".bat",
                ".cmd",
                ".ps1",
                ".scr"
            };
            
    private static readonly HashSet<string>
        ExcludedFileNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "RECON_파일_정리_지도.txt",
                "RECON_ICON_POSITION_TEST.txt"
            };
    
    

    public Task<FileScanResult> ScanAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => Scan(rootPath, cancellationToken),
            cancellationToken);
    }

    private static FileScanResult Scan(
        string rootPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException(
                "스캔할 폴더가 지정되지 않았습니다.",
                nameof(rootPath));
        }

        var normalizedRootPath =
            Path.GetFullPath(rootPath);

        if (!Directory.Exists(normalizedRootPath))
        {
            throw new DirectoryNotFoundException(
                $"폴더를 찾을 수 없습니다: " +
                normalizedRootPath);
        }

        var scannedFiles = new List<ScannedFile>();
        var errors = new List<FileScanError>();

        var skippedFileCount = 0;

        var pendingDirectories =
            new Stack<string>();

        pendingDirectories.Push(normalizedRootPath);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var currentDirectory =
                pendingDirectories.Pop();

            foreach (var subdirectory in
                     EnumerateDirectoriesSafely(
                         currentDirectory,
                         errors))
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                if (ShouldSkipDirectory(subdirectory))
                {
                    continue;
                }

                pendingDirectories.Push(subdirectory);
            }

            foreach (var filePath in
                     EnumerateFilesSafely(
                         currentDirectory,
                         errors))
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                try
                {
                    var fileInfo =
                        new FileInfo(filePath);

                    if (!fileInfo.Exists)
                    {
                        skippedFileCount++;
                        continue;
                    }

                    if (ShouldSkipFile(fileInfo))
                    {
                        skippedFileCount++;
                        continue;
                    }

                    var fileId =
                        $"F-{scannedFiles.Count + 1:D5}";

                    scannedFiles.Add(
                        new ScannedFile(
                            Id: fileId,
                            Name: fileInfo.Name,
                            Extension: fileInfo.Extension,
                            FullPath: fileInfo.FullName,
                            SizeBytes: fileInfo.Length,
                            CreationTimeUtc:
                                fileInfo.CreationTimeUtc,
                            LastWriteTimeUtc:
                                fileInfo.LastWriteTimeUtc));
                }
                catch (Exception exception)
                    when (IsExpectedFileSystemException(
                        exception))
                {
                    errors.Add(
                        new FileScanError(
                            filePath,
                            exception.Message));
                }
            }
        }

        return new FileScanResult(
            RootPath: normalizedRootPath,
            Files: scannedFiles,
            SkippedFileCount: skippedFileCount,
            Errors: errors);
    }

    private static string[]
        EnumerateDirectoriesSafely(
            string directoryPath,
            ICollection<FileScanError> errors)
    {
        try
        {
            return Directory
                .EnumerateDirectories(directoryPath)
                .ToArray();
        }
        catch (Exception exception)
            when (IsExpectedFileSystemException(
                exception))
        {
            errors.Add(
                new FileScanError(
                    directoryPath,
                    exception.Message));

            return [];
        }
    }

    private static string[] EnumerateFilesSafely(
        string directoryPath,
        ICollection<FileScanError> errors)
    {
        try
        {
            return Directory
                .EnumerateFiles(directoryPath)
                .ToArray();
        }
        catch (Exception exception)
            when (IsExpectedFileSystemException(
                exception))
        {
            errors.Add(
                new FileScanError(
                    directoryPath,
                    exception.Message));

            return [];
        }
    }

    private static bool ShouldSkipDirectory(
        string directoryPath)
    {
        try
        {
            var attributes =
                File.GetAttributes(directoryPath);

            return HasUnsafeAttributes(attributes);
        }
        catch
        {
            // 안전하게 판별할 수 없으면 들어가지 않습니다.
            return true;
        }
    }

    private static bool ShouldSkipFile(
        FileInfo fileInfo)
    {
        if (HasUnsafeAttributes(fileInfo.Attributes))
        {
            return true;
        }

        if (ExcludedFileNames.Contains(fileInfo.Name))
        {
            return true;
        }

        return ExcludedExtensions.Contains(
            fileInfo.Extension);
    }

    private static bool HasUnsafeAttributes(
        FileAttributes attributes)
    {
        const FileAttributes unsafeAttributes =
            FileAttributes.Hidden |
            FileAttributes.System |
            FileAttributes.ReparsePoint;

        return (attributes & unsafeAttributes) != 0;
    }

    private static bool
        IsExpectedFileSystemException(
            Exception exception)
    {
        return exception is
            IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException;
    }
}

internal sealed record ScannedFile(
    string Id,
    string Name,
    string Extension,
    string FullPath,
    long SizeBytes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc);

internal sealed record FileScanError(
    string Path,
    string Reason);

internal sealed record FileScanResult(
    string RootPath,
    IReadOnlyList<ScannedFile> Files,
    int SkippedFileCount,
    IReadOnlyList<FileScanError> Errors);