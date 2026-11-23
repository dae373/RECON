using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Recon.App.Services;

namespace Recon.App;

public partial class SafeCommitHistoryWindow :
    System.Windows.Window
{
    private readonly SafeCommitHistoryService
        _historyService = new();

    public SafeCommitHistoryWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        await ReloadAsync();
    }

    private async void RefreshButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        RefreshButton.IsEnabled = false;

        StatusText.Text =
            "Safe Commit 기록을 불러오는 중입니다.";

        try
        {
            var entries =
                await _historyService.LoadAsync(
                    CancellationToken.None);

            var items =
                entries
                    .Select(
                        SafeCommitHistoryItem.From)
                    .ToArray();

            HistoryGrid.ItemsSource =
                items;

            var corruptedCount =
                items.Count(item =>
                    item.HasLoadError);

            StatusText.Text =
                corruptedCount == 0
                    ? $"총 {items.Length:N0}개의 " +
                      "기록이 있습니다."
                    : $"총 {items.Length:N0}개 중 " +
                      $"{corruptedCount:N0}개의 " +
                      "기록을 읽지 못했습니다.";
        }
        catch (Exception exception)
        {
            HistoryGrid.ItemsSource =
                Array.Empty<
                    SafeCommitHistoryItem>();

            StatusText.Text =
                "기록을 불러오지 못했습니다: " +
                exception.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OpenFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Directory.CreateDirectory(
            _historyService.StorageDirectory);

        Process.Start(
            new ProcessStartInfo
            {
                FileName =
                    _historyService
                        .StorageDirectory,

                UseShellExecute = true
            });
    }

    private void CloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }
}

internal sealed class SafeCommitHistoryItem
{
    private SafeCommitHistoryItem(
        string createdAt,
        string commitId,
        string commitName,
        string status,
        string canExecute,
        int approvedCount,
        int excludedCount,
        int errorCount,
        string sourceFolder,
        string filePath,
        bool hasLoadError)
    {
        CreatedAt = createdAt;
        CommitId = commitId;
        CommitName = commitName;
        Status = status;
        CanExecute = canExecute;
        ApprovedCount = approvedCount;
        ExcludedCount = excludedCount;
        ErrorCount = errorCount;
        SourceFolder = sourceFolder;
        FilePath = filePath;
        HasLoadError = hasLoadError;
    }

    public string CreatedAt { get; }

    public string CommitId { get; }

    public string CommitName { get; }

    public string Status { get; }

    public string CanExecute { get; }

    public int ApprovedCount { get; }

    public int ExcludedCount { get; }

    public int ErrorCount { get; }

    public string SourceFolder { get; }

    public string FilePath { get; }

    public bool HasLoadError { get; }

    public static SafeCommitHistoryItem From(
        SafeCommitHistoryEntry entry)
    {
        var createdAt =
            entry.CreatedAtUtc ==
            DateTimeOffset.MinValue
                ? "알 수 없음"
                : entry.CreatedAtUtc
                    .ToLocalTime()
                    .ToString(
                        "yyyy-MM-dd HH:mm:ss");

        var status =
            entry.HasLoadError
                ? "손상됨"
                : ConvertStatus(
                    entry.Status);

        return new SafeCommitHistoryItem(
            createdAt,
            entry.CommitId,
            entry.CommitName,
            status,
            entry.CanExecute
                ? "검증 통과"
                : "잠김",
            entry.ApprovedFileCount,
            entry.ExcludedFileCount,
            entry.ScanErrorCount,
            entry.SourceFolder,
            entry.FilePath,
            entry.HasLoadError);
    }

    private static string ConvertStatus(
        string status)
    {
        return status switch
        {
            "draft" =>
                "초안",

            "validating" =>
                "검증 중",

            "awaiting_icon_snapshot" =>
                "아이콘 검증 대기",

            "ready" =>
                "안전 검증 통과",

            "validation_failed" =>
                "검증 실패",

            "executing" =>
                "실행 중",

            "completed" =>
                "완료",

            "incomplete" =>
                "미완료",

            "execution_failed" =>
                "실행 실패",

            "rolling_back" =>
                "복구 중",

            "rolled_back" =>
                "복구 완료",

            "rollback_failed" =>
                "복구 실패",

            _ =>
                status
        };
    }
}
