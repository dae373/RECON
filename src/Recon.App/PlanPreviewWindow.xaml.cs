using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Recon.App.Services;

namespace Recon.App;

public partial class PlanPreviewWindow :
    System.Windows.Window
{
    private readonly List<PlanPreviewItem> _items;

    internal PlanPreviewWindow(
        IReadOnlyList<FileClassification>
            classifications,
        string sourceFolder,
        string destinationFolder,
        int skippedFileCount,
        int errorCount)
    {
        InitializeComponent();

        _items = classifications
            .Select(PlanPreviewItem.From)
            .ToList();

        PlanGrid.ItemsSource = _items;

        SourceFolderText.Text =
            sourceFolder;

        DestinationFolderText.Text =
            destinationFolder;

        ErrorSummaryText.Text =
            $"제외 {skippedFileCount:N0}개 / " +
            $"오류 {errorCount:N0}개";

        TotalCountText.Text =
            $"{_items.Count:N0}개";

        ApprovedCountText.Text =
            $"{_items.Count(item =>
                item.IsApproved):N0}개";

        ReviewCountText.Text =
            $"{_items.Count(item =>
                !item.IsApproved):N0}개";
    }

    public int ApprovedCount =>
        _items.Count(item =>
            item.IsApproved);

    public int ReviewCount =>
        _items.Count(item =>
            !item.IsApproved);

    internal IReadOnlyList<SafeCommitPlanInput>
        CreateSafeCommitPlan()
    {
        CommitGridEdits();

        return _items
            .Select(item =>
                new SafeCommitPlanInput(
                    FileId: item.FileId,
                    FileName: item.FileName,
                    OriginalPath:
                        item.OriginalPath,
                    PlannedPath:
                        item.SuggestedPath,
                    Category:
                        item.Category,
                    Confidence:
                        item.Confidence,
                    Reason:
                        item.Reason,
                    MoveRisk:
                        item.MoveRisk,
                    ScannedSizeBytes:
                        item.ScannedSizeBytes,
                    ScannedLastWriteTimeUtc:
                        item.ScannedLastWriteTimeUtc,
                    Sha256Hash:
                        null,
                    HashStatus:
                        "pending",
                    HashReason:
                        null,
                    IsApproved:
                        item.IsApproved))
            .ToArray();
    }

    private void CommitGridEdits()
    {
        PlanGrid.CommitEdit(
            System.Windows.Controls
                .DataGridEditingUnit.Cell,
            exitEditingMode: true);

        PlanGrid.CommitEdit(
            System.Windows.Controls
                .DataGridEditingUnit.Row,
            exitEditingMode: true);
    }

    private void CancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ConfirmButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CommitGridEdits();

        DialogResult = true;
    }
}

internal sealed class PlanPreviewItem
{
    private PlanPreviewItem(
        bool isApproved,
        string fileId,
        string fileName,
        string category,
        string confidence,
        string moveRisk,
        string reason,
        string originalPath,
        string suggestedPath,
        long scannedSizeBytes,
        DateTime scannedLastWriteTimeUtc)
    {
        IsApproved = isApproved;
        FileId = fileId;
        FileName = fileName;
        Category = category;
        Confidence = confidence;
        MoveRisk = moveRisk;
        Reason = reason;
        OriginalPath = originalPath;
        SuggestedPath = suggestedPath;
        ScannedSizeBytes = scannedSizeBytes;
        ScannedLastWriteTimeUtc =
            scannedLastWriteTimeUtc;
    }

    public bool IsApproved { get; set; }

    public string FileId { get; }

    public string FileName { get; }

    public string Category { get; }

    public string Confidence { get; }

    public string MoveRisk { get; }

    public string Reason { get; }

    public string OriginalPath { get; }

    public string SuggestedPath { get; }

    public long ScannedSizeBytes { get; }

    public DateTime ScannedLastWriteTimeUtc
    {
        get;
    }

    public static PlanPreviewItem From(
        FileClassification classification)
    {
        var confidence =
            classification.Confidence switch
            {
                ClassificationConfidence.High =>
                    "높음",

                ClassificationConfidence.Medium =>
                    "중간",

                _ => "낮음"
            };

        var moveRisk =
            classification.MoveRisk switch
            {
                MoveRiskLevel.Low =>
                    "낮음",

                MoveRiskLevel.Medium =>
                    "중간",

                _ => "높음"
            };

        var suggestedPath =
            Path.Combine(
                classification.SuggestedFolder,
                classification.File.Name);

        return new PlanPreviewItem(
            !classification.RequiresReview,
            classification.File.Id,
            classification.File.Name,
            classification.Category,
            confidence,
            moveRisk,
            classification.Reason,
            classification.File.FullPath,
            suggestedPath,
            classification.File.SizeBytes,
            classification.File.LastWriteTimeUtc);
    }
}
