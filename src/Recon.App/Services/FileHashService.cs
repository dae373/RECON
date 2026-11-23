using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Recon.App.Services;

internal sealed class FileHashService
{
    public async Task<FileHashBatchResult>
        ComputeAsync(
            IReadOnlyList<SafeCommitPlanInput>
                planItems,
            MovePreflightResult preflightResult,
            CancellationToken cancellationToken)
    {
        var preflightLookup =
            preflightResult.Items
                .GroupBy(
                    item => item.Plan.FileId,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);

        var updatedPlans =
            new List<SafeCommitPlanInput>(
                planItems.Count);

        foreach (var plan in planItems)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            if (!preflightLookup.TryGetValue(
                    plan.FileId,
                    out var preflightItem))
            {
                updatedPlans.Add(
                    Fail(
                        plan,
                        "이동 전 검증 결과가 없음"));

                continue;
            }

            if (preflightItem.Status !=
                MovePreflightStatus.Ready)
            {
                updatedPlans.Add(
                    plan with
                    {
                        Sha256Hash = null,
                        HashStatus = "skipped",
                        HashReason =
                            "이동 가능 대상이 아님"
                    });

                continue;
            }

            var hashedPlan =
                await ComputeOneAsync(
                    plan,
                    cancellationToken);

            updatedPlans.Add(hashedPlan);
        }

        return new FileHashBatchResult(
            PlanItems: updatedPlans,
            ComputedCount:
                updatedPlans.Count(item =>
                    string.Equals(
                        item.HashStatus,
                        "computed",
                        StringComparison.OrdinalIgnoreCase)),
            SkippedCount:
                updatedPlans.Count(item =>
                    string.Equals(
                        item.HashStatus,
                        "skipped",
                        StringComparison.OrdinalIgnoreCase)),
            FailedCount:
                updatedPlans.Count(item =>
                    string.Equals(
                        item.HashStatus,
                        "failed",
                        StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<SafeCommitPlanInput>
        ComputeOneAsync(
            SafeCommitPlanInput plan,
            CancellationToken cancellationToken)
    {
        try
        {
            var before =
                new FileInfo(plan.OriginalPath);

            before.Refresh();

            if (!before.Exists)
            {
                return Fail(
                    plan,
                    "해시 계산 전 원본 파일이 사라짐");
            }

            if (before.Length !=
                plan.ScannedSizeBytes)
            {
                return Fail(
                    plan,
                    "해시 계산 전 파일 크기가 변경됨");
            }

            if (before.LastWriteTimeUtc !=
                plan.ScannedLastWriteTimeUtc)
            {
                return Fail(
                    plan,
                    "해시 계산 전 수정 시간이 변경됨");
            }

            byte[] hashBytes;

            await using (var stream =
                new FileStream(
                    plan.OriginalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 131072,
                    options:
                        FileOptions.Asynchronous |
                        FileOptions.SequentialScan))
            {
                hashBytes =
                    await SHA256.HashDataAsync(
                        stream,
                        cancellationToken);
            }

            var after =
                new FileInfo(plan.OriginalPath);

            after.Refresh();

            if (!after.Exists)
            {
                return Fail(
                    plan,
                    "해시 계산 후 원본 파일이 사라짐");
            }

            if (after.Length != before.Length)
            {
                return Fail(
                    plan,
                    "해시 계산 중 파일 크기가 변경됨");
            }

            if (after.LastWriteTimeUtc !=
                before.LastWriteTimeUtc)
            {
                return Fail(
                    plan,
                    "해시 계산 중 수정 시간이 변경됨");
            }

            var hash =
                Convert.ToHexString(hashBytes)
                    .ToLowerInvariant();

            return plan with
            {
                Sha256Hash = hash,
                HashStatus = "computed",
                HashReason = null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is
                IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or CryptographicException)
        {
            return Fail(
                plan,
                $"SHA-256 계산 실패: " +
                exception.Message);
        }
    }

    private static SafeCommitPlanInput Fail(
        SafeCommitPlanInput plan,
        string reason)
    {
        return plan with
        {
            Sha256Hash = null,
            HashStatus = "failed",
            HashReason = reason
        };
    }
}

internal sealed record FileHashBatchResult(
    IReadOnlyList<SafeCommitPlanInput> PlanItems,
    int ComputedCount,
    int SkippedCount,
    int FailedCount);
