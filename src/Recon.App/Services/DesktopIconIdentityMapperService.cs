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

internal sealed class DesktopIconIdentityMapperService
{
    private static readonly JsonSerializerOptions
        JsonOptions = new()
        {
            WriteIndented = true,

            Encoder =
                JavaScriptEncoder.Create(
                    UnicodeRanges.All)
        };

    private static readonly HashSet<string>
        ProtectedExecutableExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".exe",
                ".msi",
                ".msix",
                ".appx",
                ".appxbundle",
                ".com",
                ".bat",
                ".cmd",
                ".ps1",
                ".scr"
            };

    private static readonly HashSet<string>
        ShortcutExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".lnk",
                ".url"
            };

    public async Task<
        DesktopIconIdentityMappingResult>
        MapAsync(
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
            var document =
                await LoadDocumentAsync(
                    safeCommitPath,
                    cancellationToken);

            if (document["desktopIconSnapshot"]
                is not JsonObject snapshot)
            {
                return Failure(
                    "아이콘 스냅샷이 없습니다.");
            }

            if (snapshot["items"]
                is not JsonArray iconItems)
            {
                return Failure(
                    "아이콘 목록이 없습니다.");
            }

            var candidates =
                EnumerateDesktopEntries();

            var mappedCount = 0;
            var ambiguousCount = 0;
            var unmappedCount = 0;
            var protectedCount = 0;
            var movableCount = 0;

            foreach (var node in iconItems)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                if (node is not JsonObject icon)
                {
                    continue;
                }

                var iconName =
                    ReadString(
                        icon,
                        "name");

                if (string.IsNullOrWhiteSpace(
                        iconName))
                {
                    MarkUnmapped(
                        icon,
                        "아이콘 이름이 없음");

                    unmappedCount++;
                    protectedCount++;

                    continue;
                }

                var exactMatches =
                    candidates
                        .Where(candidate =>
                            string.Equals(
                                candidate.FileName,
                                iconName,
                                StringComparison
                                    .OrdinalIgnoreCase))
                        .ToArray();

                var matches =
                    exactMatches.Length > 0
                        ? exactMatches
                        : candidates
                            .Where(candidate =>
                                string.Equals(
                                    candidate
                                        .NameWithoutExtension,
                                    iconName,
                                    StringComparison
                                        .OrdinalIgnoreCase))
                            .ToArray();

                if (matches.Length == 0)
                {
                    MarkUnmapped(
                        icon,
                        "대응하는 바탕화면 파일을 " +
                        "찾지 못함");

                    unmappedCount++;
                    protectedCount++;

                    continue;
                }

                if (matches.Length > 1)
                {
                    MarkAmbiguous(
                        icon,
                        matches);

                    ambiguousCount++;
                    protectedCount++;

                    continue;
                }

                var matched =
                    matches[0];

                var classification =
                    ClassifyEntry(matched);

                icon["resolvedPath"] =
                    matched.FullPath;

                icon["itemType"] =
                    classification.ItemType;

                icon["protected"] =
                    classification.IsProtected;

                icon["identityMatchStatus"] =
                    "matched";

                icon["identityMatchReason"] =
                    classification.Reason;

                mappedCount++;

                if (classification.IsProtected)
                {
                    protectedCount++;
                }
                else
                {
                    movableCount++;
                }
            }

            var mappingSafe =
                ambiguousCount == 0;

            var now =
                DateTimeOffset.UtcNow;

            document["schemaVersion"] =
                Math.Max(
                    ReadInteger(
                        document,
                        "schemaVersion"),
                    8);

            document[
                "desktopIconIdentityMappingCompleted"] =
                true;

            document[
                "desktopIconIdentityMappingSafe"] =
                mappingSafe;

            document[
                "desktopIconIdentityMappedAtUtc"] =
                now.ToString("O");

            document[
                "desktopIconIdentityMapping"] =
                new JsonObject
                {
                    ["mappedCount"] =
                        mappedCount,

                    ["ambiguousCount"] =
                        ambiguousCount,

                    ["unmappedCount"] =
                        unmappedCount,

                    ["protectedCount"] =
                        protectedCount,

                    ["movableCount"] =
                        movableCount,

                    ["isSafe"] =
                        mappingSafe,

                    ["reason"] =
                        mappingSafe
                            ? "모호한 아이콘 연결이 없습니다."
                            : "같은 이름의 바탕화면 항목이 " +
                              "여러 개 존재합니다."
                };

            document["updatedAtUtc"] =
                now.ToString("O");

            await WriteDocumentAtomicallyAsync(
                safeCommitPath,
                document,
                cancellationToken);

            return new DesktopIconIdentityMappingResult(
                IsSuccessful: true,
                IsSafe: mappingSafe,
                MappedCount: mappedCount,
                AmbiguousCount: ambiguousCount,
                UnmappedCount: unmappedCount,
                ProtectedCount: protectedCount,
                MovableCount: movableCount,
                Reason:
                    mappingSafe
                        ? "아이콘과 바탕화면 항목 연결을 " +
                          "완료했습니다."
                        : "모호한 아이콘 연결이 있어 " +
                          "자동 이동을 허용할 수 없습니다.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                "아이콘 식별 정보 저장 실패: " +
                exception.Message);
        }
    }

    private static IReadOnlyList<
        DesktopEntryCandidate>
        EnumerateDesktopEntries()
    {
        var roots =
            new[]
            {
                Environment.GetFolderPath(
                    Environment.SpecialFolder
                        .DesktopDirectory),

                Environment.GetFolderPath(
                    Environment.SpecialFolder
                        .CommonDesktopDirectory)
            }
            .Where(path =>
                !string.IsNullOrWhiteSpace(path))
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidates =
            new List<DesktopEntryCandidate>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] entries;

            try
            {
                entries =
                    Directory
                        .EnumerateFileSystemEntries(
                            root,
                            "*",
                            SearchOption
                                .TopDirectoryOnly)
                        .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    var isDirectory =
                        Directory.Exists(entry);

                    var fileName =
                        Path.GetFileName(entry);

                    var nameWithoutExtension =
                        isDirectory
                            ? fileName
                            : Path
                                .GetFileNameWithoutExtension(
                                    entry);

                    candidates.Add(
                        new DesktopEntryCandidate(
                            FullPath:
                                Path.GetFullPath(entry),
                            FileName:
                                fileName,
                            NameWithoutExtension:
                                nameWithoutExtension,
                            Extension:
                                isDirectory
                                    ? ""
                                    : Path.GetExtension(entry),
                            IsDirectory:
                                isDirectory));
                }
                catch
                {
                    // 읽을 수 없는 항목은 제외합니다.
                }
            }
        }

        return candidates;
    }

    private static EntryClassification
        ClassifyEntry(
            DesktopEntryCandidate candidate)
    {
        if (candidate.IsDirectory)
        {
            return new EntryClassification(
                ItemType: "folder",
                IsProtected: true,
                Reason:
                    "기존 바탕화면 폴더는 보호 대상");
        }

        if (ShortcutExtensions.Contains(
                candidate.Extension))
        {
            return new EntryClassification(
                ItemType: "shortcut",
                IsProtected: true,
                Reason:
                    "바로가기는 보호 대상");
        }

        if (ProtectedExecutableExtensions.Contains(
                candidate.Extension))
        {
            return new EntryClassification(
                ItemType: "executable",
                IsProtected: true,
                Reason:
                    "실행 파일 또는 설치 파일은 " +
                    "보호 대상");
        }

        if (string.Equals(
                candidate.FileName,
                "RECON_파일_정리_지도.txt",
                StringComparison.OrdinalIgnoreCase))
        {
            return new EntryClassification(
                ItemType: "recon_generated",
                IsProtected: true,
                Reason:
                    "RECON 생성 파일은 보호 대상");
        }

        return new EntryClassification(
            ItemType: "regular_file",
            IsProtected: false,
            Reason:
                "일반 바탕화면 파일");
    }

    private static void MarkUnmapped(
        JsonObject icon,
        string reason)
    {
        icon["resolvedPath"] =
            null;

        icon["itemType"] =
            "unknown";

        icon["protected"] =
            true;

        icon["identityMatchStatus"] =
            "unmapped";

        icon["identityMatchReason"] =
            reason;
    }

    private static void MarkAmbiguous(
        JsonObject icon,
        IReadOnlyList<DesktopEntryCandidate>
            matches)
    {
        icon["resolvedPath"] =
            null;

        icon["itemType"] =
            "unknown";

        icon["protected"] =
            true;

        icon["identityMatchStatus"] =
            "ambiguous";

        icon["identityMatchReason"] =
            "동일한 표시 이름을 가진 바탕화면 " +
            "항목이 여러 개 존재함";

        var candidatePaths =
            new JsonArray();

        foreach (var match in matches)
        {
            candidatePaths.Add(
                match.FullPath);
        }

        icon["identityCandidates"] =
            candidatePaths;
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
        JsonObject item,
        string propertyName)
    {
        try
        {
            return item[propertyName]
                ?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static int ReadInteger(
        JsonObject item,
        string propertyName)
    {
        try
        {
            return item[propertyName]
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
        DesktopIconIdentityMappingResult
        Failure(
            string reason)
    {
        return new DesktopIconIdentityMappingResult(
            IsSuccessful: false,
            IsSafe: false,
            MappedCount: 0,
            AmbiguousCount: 0,
            UnmappedCount: 0,
            ProtectedCount: 0,
            MovableCount: 0,
            Reason: reason);
    }

    private sealed record DesktopEntryCandidate(
        string FullPath,
        string FileName,
        string NameWithoutExtension,
        string Extension,
        bool IsDirectory);

    private sealed record EntryClassification(
        string ItemType,
        bool IsProtected,
        string Reason);
}

internal sealed record
    DesktopIconIdentityMappingResult(
        bool IsSuccessful,
        bool IsSafe,
        int MappedCount,
        int AmbiguousCount,
        int UnmappedCount,
        int ProtectedCount,
        int MovableCount,
        string Reason);
