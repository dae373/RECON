using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Recon.App.Services;

internal enum MovePreflightStatus
{
    Ready,
    Excluded,
    Blocked
}

internal sealed record MovePreflightItem(
    SafeCommitPlanInput Plan,
    MovePreflightStatus Status,
    string Reason);

internal sealed record MovePreflightResult(
    IReadOnlyList<MovePreflightItem> Items,
    int ReadyCount,
    int ExcludedCount,
    int BlockedCount);

internal sealed class MovePreflightValidator
{
    public Task<MovePreflightResult> ValidateAsync(
        IReadOnlyList<SafeCommitPlanInput>
            planItems,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => Validate(
                planItems,
                destinationRoot,
                cancellationToken),
            cancellationToken);
    }

    private static MovePreflightResult Validate(
        IReadOnlyList<SafeCommitPlanInput>
            planItems,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(
                destinationRoot))
        {
            throw new ArgumentException(
                "정리 목적지가 지정되지 않았습니다.",
                nameof(destinationRoot));
        }

        var normalizedDestinationRoot =
            NormalizeDirectoryPath(
                destinationRoot);

        var duplicateDestinations =
            planItems
                .Where(item => item.IsApproved)
                .GroupBy(
                    item => NormalizePathSafely(
                        item.PlannedPath),
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);

        var results =
            new List<MovePreflightItem>();

        foreach (var plan in planItems)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            results.Add(
                ValidatePlan(
                    plan,
                    normalizedDestinationRoot,
                    duplicateDestinations));
        }

        return new MovePreflightResult(
            Items: results,
            ReadyCount:
                results.Count(item =>
                    item.Status ==
                    MovePreflightStatus.Ready),
            ExcludedCount:
                results.Count(item =>
                    item.Status ==
                    MovePreflightStatus.Excluded),
            BlockedCount:
                results.Count(item =>
                    item.Status ==
                    MovePreflightStatus.Blocked));
    }

    private static MovePreflightItem ValidatePlan(
        SafeCommitPlanInput plan,
        string destinationRoot,
        IReadOnlySet<string>
            duplicateDestinations)
    {
        if (!plan.IsApproved)
        {
            return new MovePreflightItem(
                plan,
                MovePreflightStatus.Excluded,
                "사용자가 이동에서 제외함");
        }

        if (string.IsNullOrWhiteSpace(
                plan.OriginalPath))
        {
            return Block(
                plan,
                "원본 경로가 없음");
        }

        if (string.IsNullOrWhiteSpace(
                plan.PlannedPath))
        {
            return Block(
                plan,
                "이동 예정 경로가 없음");
        }

        string originalPath;
        string plannedPath;

        try
        {
            originalPath =
                Path.GetFullPath(
                    plan.OriginalPath);

            plannedPath =
                Path.GetFullPath(
                    plan.PlannedPath);
        }
        catch (Exception exception)
            when (exception is
                ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return Block(
                plan,
                "파일 경로가 올바르지 않음");
        }

        if (!File.Exists(originalPath))
        {
            return Block(
                plan,
                "원본 파일을 찾을 수 없음");
        }

        FileInfo currentFileInfo;

        try
        {
            currentFileInfo =
                new FileInfo(originalPath);

            currentFileInfo.Refresh();

            if (!currentFileInfo.Exists)
            {
                return Block(
                    plan,
                    "원본 파일을 찾을 수 없음");
            }
        }
        catch (Exception exception)
            when (exception is
                IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return Block(
                plan,
                "현재 파일 정보를 읽을 수 없음");
        }

        if (currentFileInfo.Length !=
            plan.ScannedSizeBytes)
        {
            return Block(
                plan,
                "스캔 후 파일 크기가 변경됨");
        }

        if (currentFileInfo.LastWriteTimeUtc !=
            plan.ScannedLastWriteTimeUtc)
        {
            return Block(
                plan,
                "스캔 후 파일 수정 시간이 변경됨");
        }


        if (string.Equals(
                plan.MoveRisk,
                "높음",
                StringComparison.OrdinalIgnoreCase))
        {
            return Block(
                plan,
                "이동 위험도가 높음");
        }

        if (!IsPathInsideDirectory(
                plannedPath,
                destinationRoot))
        {
            return Block(
                plan,
                "이동 예정 경로가 RECON 정리함 " +
                "외부에 있음");
        }

        if (string.Equals(
                originalPath,
                plannedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return Block(
                plan,
                "원본 경로와 목적지 경로가 동일함");
        }

        var normalizedPlannedPath =
            NormalizePathSafely(plannedPath);

        if (duplicateDestinations.Contains(
                normalizedPlannedPath))
        {
            return Block(
                plan,
                "여러 파일의 이동 예정 경로가 동일함");
        }

        if (File.Exists(plannedPath)
            || Directory.Exists(plannedPath))
        {
            return Block(
                plan,
                "목적지에 같은 이름의 항목이 존재함");
        }

        if (!IsDestinationDriveAvailable(
                plannedPath))
        {
            return Block(
                plan,
                "목적지 드라이브에 접근할 수 없음");
        }

        if (!CanOpenExclusively(originalPath))
        {
            return Block(
                plan,
                "파일이 사용 중이거나 읽을 수 없음");
        }

        return new MovePreflightItem(
            plan,
            MovePreflightStatus.Ready,
            "현재 조건에서 이동 가능한 후보");
    }

    private static MovePreflightItem Block(
        SafeCommitPlanInput plan,
        string reason)
    {
        return new MovePreflightItem(
            plan,
            MovePreflightStatus.Blocked,
            reason);
    }

    private static bool CanOpenExclusively(
        string path)
    {
        try
        {
            using var stream =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);

            return stream.CanRead;
        }
        catch (Exception exception)
            when (exception is
                IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static bool
        IsDestinationDriveAvailable(
            string path)
    {
        var root =
            Path.GetPathRoot(path);

        return !string.IsNullOrWhiteSpace(root)
               && Directory.Exists(root);
    }

    private static bool IsPathInsideDirectory(
        string path,
        string directory)
    {
        var normalizedDirectory =
            NormalizeDirectoryPath(directory);

        var normalizedPath =
            Path.GetFullPath(path);

        return normalizedPath.StartsWith(
            normalizedDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string
        NormalizeDirectoryPath(
            string path)
    {
        var normalized =
            Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

        return normalized +
               Path.DirectorySeparatorChar;
    }

    private static string NormalizePathSafely(
        string path)
    {
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
}
