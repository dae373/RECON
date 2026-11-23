using System.Linq;
using System.Windows;
using Recon.App.Services;

namespace Recon.App;

public partial class MovePreflightWindow :
    System.Windows.Window
{
    internal MovePreflightWindow(
        MovePreflightResult result)
    {
        InitializeComponent();

        ReadyCountText.Text =
            $"{result.ReadyCount:N0}개";

        ExcludedCountText.Text =
            $"{result.ExcludedCount:N0}개";

        BlockedCountText.Text =
            $"{result.BlockedCount:N0}개";

        PreflightGrid.ItemsSource =
            result.Items
                .Select(
                    MovePreflightDisplayItem.From)
                .OrderBy(item =>
                    item.SortOrder)
                .ThenBy(item =>
                    item.FileName)
                .ToArray();
    }

    private void BackButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ContinueButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        DialogResult = true;
    }
}

internal sealed class MovePreflightDisplayItem
{
    private MovePreflightDisplayItem(
        int sortOrder,
        string status,
        string fileId,
        string fileName,
        string reason,
        string originalPath,
        string plannedPath)
    {
        SortOrder = sortOrder;
        Status = status;
        FileId = fileId;
        FileName = fileName;
        Reason = reason;
        OriginalPath = originalPath;
        PlannedPath = plannedPath;
    }

    public int SortOrder { get; }

    public string Status { get; }

    public string FileId { get; }

    public string FileName { get; }

    public string Reason { get; }

    public string OriginalPath { get; }

    public string PlannedPath { get; }

    public static MovePreflightDisplayItem From(
        MovePreflightItem item)
    {
        var status = item.Status switch
        {
            MovePreflightStatus.Ready =>
                "이동 가능",

            MovePreflightStatus.Excluded =>
                "사용자 제외",

            _ => "안전 차단"
        };

        var sortOrder = item.Status switch
        {
            MovePreflightStatus.Blocked => 0,
            MovePreflightStatus.Ready => 1,
            _ => 2
        };

        return new MovePreflightDisplayItem(
            sortOrder,
            status,
            item.Plan.FileId,
            item.Plan.FileName,
            item.Reason,
            item.Plan.OriginalPath,
            item.Plan.PlannedPath);
    }
}
