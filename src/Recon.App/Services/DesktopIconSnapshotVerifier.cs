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

internal sealed class DesktopIconSnapshotVerifier
{
    private const double PositionTolerance = 2.0;

    private static readonly JsonSerializerOptions
        JsonOptions = new()
        {
            WriteIndented = true,

            Encoder =
                JavaScriptEncoder.Create(
                    UnicodeRanges.All)
        };

    private readonly DesktopIconProbeService
        _probeService = new();

    public async Task<
        DesktopIconReadbackVerificationResult>
        VerifyAsync(
            string safeCommitPath,
            CancellationToken cancellationToken)
    {
        if (!File.Exists(safeCommitPath))
        {
            return Failure(
                "Safe Commit 파일을 찾을 수 없습니다.");
        }

        JsonObject document;

        try
        {
            document =
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

        if (document["desktopIconSnapshot"]
            is not JsonObject snapshot)
        {
            return Failure(
                "저장된 아이콘 스냅샷이 없습니다.");
        }

        if (snapshot["items"]
            is not JsonArray savedItemsArray)
        {
            return Failure(
                "저장된 아이콘 목록이 없습니다.");
        }

        var savedItems =
            ReadSavedItems(savedItemsArray);

        if (savedItems.Count == 0)
        {
            return Failure(
                "저장된 아이콘 항목이 비어 있습니다.");
        }

        DesktopIconProbeResult currentProbe;

        try
        {
            currentProbe =
                await Task.Run(
                    _probeService.Probe,
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                "현재 아이콘 좌표를 읽지 못했습니다: " +
                exception.Message);
        }

        if (!currentProbe.IsSuccessful)
        {
            return Failure(
                currentProbe.Reason);
        }

        var verification =
            Compare(
                savedItems,
                currentProbe.Icons);

        try
        {
            await SaveVerificationAsync(
                safeCommitPath,
                document,
                verification,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return verification with
            {
                IsSuccessful = false,

                Reason =
                    verification.Reason +
                    "\n검증 결과 저장 실패: " +
                    exception.Message
            };
        }

        return verification;
    }

    private static IReadOnlyList<SavedIcon>
        ReadSavedItems(
            JsonArray items)
    {
        var result =
            new List<SavedIcon>();

        foreach (var node in items)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var name =
                ReadString(item, "name");

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            result.Add(
                new SavedIcon(
                    Name: name,
                    X: ReadDouble(item, "x"),
                    Y: ReadDouble(item, "y"),
                    Width:
                        ReadDouble(item, "width"),
                    Height:
                        ReadDouble(item, "height")));
        }

        return result;
    }

    private static
        DesktopIconReadbackVerificationResult
        Compare(
            IReadOnlyList<SavedIcon> savedItems,
            IReadOnlyList<DesktopIconProbeItem>
                currentItems)
    {
        var matchedCount = 0;
        var movedCount = 0;
        var missingCount = 0;
        var extraCount = 0;

        var issues =
            new List<DesktopIconReadbackIssue>();

        var allNames =
            savedItems
                .Select(item => item.Name)
                .Concat(
                    currentItems.Select(item =>
                        item.Name))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .ToArray();

        foreach (var name in allNames)
        {
            var savedGroup =
                savedItems
                    .Where(item =>
                        string.Equals(
                            item.Name,
                            name,
                            StringComparison
                                .OrdinalIgnoreCase))
                    .OrderBy(item => item.Y)
                    .ThenBy(item => item.X)
                    .ToArray();

            var currentGroup =
                currentItems
                    .Where(item =>
                        string.Equals(
                            item.Name,
                            name,
                            StringComparison
                                .OrdinalIgnoreCase))
                    .OrderBy(item => item.Y)
                    .ThenBy(item => item.X)
                    .ToArray();

            var pairCount =
                Math.Min(
                    savedGroup.Length,
                    currentGroup.Length);

            for (var index = 0;
                 index < pairCount;
                 index++)
            {
                var saved =
                    savedGroup[index];

                var current =
                    currentGroup[index];

                if (CoordinatesMatch(
                        saved,
                        current))
                {
                    matchedCount++;
                    continue;
                }

                movedCount++;

                issues.Add(
                    new DesktopIconReadbackIssue(
                        Name: name,
                        IssueType: "moved",
                        Reason:
                            $"좌표 변경: " +
                            $"({saved.X:N0}, " +
                            $"{saved.Y:N0}) → " +
                            $"({current.X:N0}, " +
                            $"{current.Y:N0})"));
            }

            if (savedGroup.Length >
                currentGroup.Length)
            {
                var missing =
                    savedGroup.Length -
                    currentGroup.Length;

                missingCount += missing;

                issues.Add(
                    new DesktopIconReadbackIssue(
                        Name: name,
                        IssueType: "missing",
                        Reason:
                            $"{missing:N0}개의 저장된 " +
                            "아이콘을 찾지 못함"));
            }

            if (currentGroup.Length >
                savedGroup.Length)
            {
                var extra =
                    currentGroup.Length -
                    savedGroup.Length;

                extraCount += extra;

                issues.Add(
                    new DesktopIconReadbackIssue(
                        Name: name,
                        IssueType: "extra",
                        Reason:
                            $"{extra:N0}개의 새 아이콘이 " +
                            "추가됨"));
            }
        }

        var successful =
            movedCount == 0
            && missingCount == 0
            && extraCount == 0
            && matchedCount == savedItems.Count;

        var reason =
            successful
                ? "저장한 아이콘 이름과 좌표가 " +
                  "현재 바탕화면과 일치합니다."
                : "저장된 아이콘 스냅샷과 현재 " +
                  "바탕화면이 다릅니다.";

        return new DesktopIconReadbackVerificationResult(
            IsSuccessful: successful,
            MatchedCount: matchedCount,
            MovedCount: movedCount,
            MissingCount: missingCount,
            ExtraCount: extraCount,
            Reason: reason,
            Issues: issues);
    }

    private static bool CoordinatesMatch(
        SavedIcon saved,
        DesktopIconProbeItem current)
    {
        return Math.Abs(saved.X - current.X)
                   <= PositionTolerance
               &&
               Math.Abs(saved.Y - current.Y)
                   <= PositionTolerance
               &&
               Math.Abs(saved.Width - current.Width)
                   <= PositionTolerance
               &&
               Math.Abs(saved.Height - current.Height)
                   <= PositionTolerance;
    }

    private static async Task SaveVerificationAsync(
        string safeCommitPath,
        JsonObject document,
        DesktopIconReadbackVerificationResult
            verification,
        CancellationToken cancellationToken)
    {
        var now =
            DateTimeOffset.UtcNow;

        var issueArray =
            new JsonArray();

        foreach (var issue in verification.Issues)
        {
            issueArray.Add(
                new JsonObject
                {
                    ["name"] =
                        issue.Name,

                    ["issueType"] =
                        issue.IssueType,

                    ["reason"] =
                        issue.Reason
                });
        }

        document["schemaVersion"] =
            Math.Max(
                ReadInteger(
                    document,
                    "schemaVersion"),
                7);

        document[
            "desktopIconSnapshotReadbackVerified"] =
            verification.IsSuccessful;

        document[
            "desktopIconSnapshotReadbackVerifiedAtUtc"] =
            now.ToString("O");

        document[
            "desktopIconSnapshotReadbackVerification"] =
            new JsonObject
            {
                ["matchedCount"] =
                    verification.MatchedCount,

                ["movedCount"] =
                    verification.MovedCount,

                ["missingCount"] =
                    verification.MissingCount,

                ["extraCount"] =
                    verification.ExtraCount,

                ["positionTolerancePixels"] =
                    PositionTolerance,

                ["isSuccessful"] =
                    verification.IsSuccessful,

                ["reason"] =
                    verification.Reason,

                ["issues"] =
                    issueArray
            };

        document["updatedAtUtc"] =
            now.ToString("O");

        await WriteDocumentAtomicallyAsync(
            safeCommitPath,
            document,
            cancellationToken);
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
                   "Safe Commit JSON이 올바르지 않습니다.");
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

    private static double ReadDouble(
        JsonObject item,
        string propertyName)
    {
        try
        {
            return item[propertyName]
                ?.GetValue<double>() ?? 0;
        }
        catch
        {
            return 0;
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
        DesktopIconReadbackVerificationResult
        Failure(
            string reason)
    {
        return new DesktopIconReadbackVerificationResult(
            IsSuccessful: false,
            MatchedCount: 0,
            MovedCount: 0,
            MissingCount: 0,
            ExtraCount: 0,
            Reason: reason,
            Issues:
                Array.Empty<
                    DesktopIconReadbackIssue>());
    }

    private sealed record SavedIcon(
        string Name,
        double X,
        double Y,
        double Width,
        double Height);
}

internal sealed record
    DesktopIconReadbackVerificationResult(
        bool IsSuccessful,
        int MatchedCount,
        int MovedCount,
        int MissingCount,
        int ExtraCount,
        string Reason,
        IReadOnlyList<
            DesktopIconReadbackIssue> Issues);

internal sealed record DesktopIconReadbackIssue(
    string Name,
    string IssueType,
    string Reason);
