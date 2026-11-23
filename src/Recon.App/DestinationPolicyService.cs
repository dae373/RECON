using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Recon.App.Services;

internal sealed class DestinationPolicyService
{
    private const string DefaultFolderName =
        "RECON 정리함";

    public string GetDefaultDestinationRoot()
    {
        var userProfile =
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidOperationException(
                "사용자 프로필 폴더를 찾을 수 없습니다.");
        }

        return Path.Combine(
            userProfile,
            DefaultFolderName);
    }

    public DestinationPolicyResult Validate(
        string sourceFolder,
        string destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            return DestinationPolicyResult.Deny(
                "분석한 원본 폴더가 없습니다.");
        }

        if (string.IsNullOrWhiteSpace(
                destinationRoot))
        {
            return DestinationPolicyResult.Deny(
                "정리 목적지가 없습니다.");
        }

        string normalizedSource;
        string normalizedDestination;

        try
        {
            normalizedSource =
                NormalizeDirectoryPath(
                    sourceFolder);

            normalizedDestination =
                NormalizeDirectoryPath(
                    destinationRoot);
        }
        catch (Exception exception)
            when (exception is
                ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return DestinationPolicyResult.Deny(
                "정리 목적지 경로가 올바르지 않습니다.");
        }

        if (string.Equals(
                normalizedSource,
                normalizedDestination,
                StringComparison.OrdinalIgnoreCase))
        {
            return DestinationPolicyResult.Deny(
                "원본 폴더와 정리 목적지가 동일합니다.");
        }

        if (IsInsideDirectory(
                normalizedDestination,
                normalizedSource))
        {
            return DestinationPolicyResult.Deny(
                "정리 목적지가 분석 폴더 내부에 있습니다. " +
                "재스캔과 순환 이동 위험이 있습니다.");
        }

        if (IsInsideDirectory(
                normalizedSource,
                normalizedDestination))
        {
            return DestinationPolicyResult.Deny(
                "분석 폴더가 정리 목적지 내부에 있습니다.");
        }

        var protectedRoot =
            FindProtectedSystemRoot(
                normalizedDestination);

        if (protectedRoot is not null)
        {
            return DestinationPolicyResult.Deny(
                $"시스템 보호 경로에는 정리 폴더를 " +
                $"만들 수 없습니다: {protectedRoot}");
        }

        var cloudRoot =
            FindCloudSyncRoot(
                normalizedDestination);

        if (cloudRoot is not null)
        {
            return DestinationPolicyResult.DenyCloud(
                $"선택한 목적지는 클라우드 동기화 " +
                $"경로입니다: {cloudRoot}");
        }

        return DestinationPolicyResult.Allow(
            normalizedDestination);
    }

    private static string? FindCloudSyncRoot(
        string destinationPath)
    {
        var cloudRoots =
            new[]
            {
                Environment.GetEnvironmentVariable(
                    "OneDrive"),

                Environment.GetEnvironmentVariable(
                    "OneDriveConsumer"),

                Environment.GetEnvironmentVariable(
                    "OneDriveCommercial")
            }
            .Where(path =>
                !string.IsNullOrWhiteSpace(path))
            .Select(path =>
                NormalizeDirectoryPath(path!))
            .Distinct(
                StringComparer.OrdinalIgnoreCase);

        foreach (var cloudRoot in cloudRoots)
        {
            if (IsInsideDirectory(
                    destinationPath,
                    cloudRoot)
                ||
                string.Equals(
                    destinationPath,
                    cloudRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return cloudRoot;
            }
        }

        var pathParts =
            destinationPath.Split(
                [
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                ],
                StringSplitOptions
                    .RemoveEmptyEntries);

        if (pathParts.Any(part =>
                part.StartsWith(
                    "OneDrive",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return "OneDrive";
        }

        return null;
    }

    private static string? FindProtectedSystemRoot(
        string destinationPath)
    {
        var protectedRoots =
            new List<string?>
            {
                Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows),

                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),

                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86),

                Environment.GetFolderPath(
                    Environment.SpecialFolder
                        .CommonApplicationData)
            };

        foreach (var root in protectedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalizedRoot =
                NormalizeDirectoryPath(root);

            if (IsInsideDirectory(
                    destinationPath,
                    normalizedRoot)
                ||
                string.Equals(
                    destinationPath,
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return normalizedRoot;
            }
        }

        return null;
    }

    private static bool IsInsideDirectory(
        string childPath,
        string parentPath)
    {
        var normalizedChild =
            NormalizeDirectoryPath(childPath);

        var normalizedParent =
            NormalizeDirectoryPath(parentPath);

        return normalizedChild.StartsWith(
            normalizedParent,
            StringComparison.OrdinalIgnoreCase)
            &&
            !string.Equals(
                normalizedChild,
                normalizedParent,
                StringComparison.OrdinalIgnoreCase);
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
}

internal sealed record DestinationPolicyResult(
    bool IsAllowed,
    bool IsCloudSynced,
    string DestinationRoot,
    string Reason)
{
    public static DestinationPolicyResult Allow(
        string destinationRoot)
    {
        return new DestinationPolicyResult(
            IsAllowed: true,
            IsCloudSynced: false,
            DestinationRoot: destinationRoot,
            Reason:
                "로컬 정리 목적지를 사용할 수 있습니다.");
    }

    public static DestinationPolicyResult Deny(
        string reason)
    {
        return new DestinationPolicyResult(
            IsAllowed: false,
            IsCloudSynced: false,
            DestinationRoot: "",
            Reason: reason);
    }

    public static DestinationPolicyResult DenyCloud(
        string reason)
    {
        return new DestinationPolicyResult(
            IsAllowed: false,
            IsCloudSynced: true,
            DestinationRoot: "",
            Reason: reason);
    }
}
