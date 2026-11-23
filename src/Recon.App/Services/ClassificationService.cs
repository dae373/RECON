using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Recon.App.Services;

internal enum ClassificationConfidence
{
    High,
    Medium,
    Low
}

internal enum MoveRiskLevel
{
    Low,
    Medium,
    High
}

internal sealed record FileClassification(
    ScannedFile File,
    string Category,
    string SuggestedFolder,
    ClassificationConfidence Confidence,
    string Reason,
    MoveRiskLevel MoveRisk,
    bool RequiresReview);

internal sealed class RuleBasedFileClassifier
{
    private static readonly HashSet<string>
        DocumentExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".pdf",
                ".doc",
                ".docx",
                ".txt",
                ".rtf",
                ".odt",
                ".hwp",
                ".hwpx",
                ".ppt",
                ".pptx",
                ".xls",
                ".xlsx",
                ".csv",
                ".md"
            };

    private static readonly HashSet<string>
        ImageExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".jpg",
                ".jpeg",
                ".png",
                ".gif",
                ".bmp",
                ".webp",
                ".heic",
                ".tif",
                ".tiff"
            };

    private static readonly HashSet<string>
        ArchiveExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".zip",
                ".7z",
                ".rar",
                ".tar",
                ".gz",
                ".bz2",
                ".xz"
            };

    private static readonly HashSet<string>
        InstallerExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".msi",
                ".msix",
                ".appx",
                ".appxbundle"
            };

    private static readonly HashSet<string>
        MediaExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".mp3",
                ".wav",
                ".flac",
                ".aac",
                ".m4a",
                ".ogg",
                ".mp4",
                ".mov",
                ".avi",
                ".mkv",
                ".webm"
            };

    private static readonly HashSet<string>
        DevelopmentExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".cs",
                ".xaml",
                ".json",
                ".xml",
                ".yaml",
                ".yml",
                ".js",
                ".ts",
                ".tsx",
                ".jsx",
                ".py",
                ".java",
                ".cpp",
                ".c",
                ".h",
                ".hpp",
                ".sql",
                ".html",
                ".css",
                ".scss"
            };

    private static readonly HashSet<string>
        HighRiskDevelopmentExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".sln",
                ".slnx",
                ".csproj",
                ".vbproj",
                ".fsproj",
                ".props",
                ".targets",
                ".config",
                ".env"
            };

    private static readonly HashSet<string>
        TemporaryExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".tmp",
                ".temp",
                ".bak",
                ".old",
                ".cache",
                ".part",
                ".crdownload"
            };

    private static readonly HashSet<string>
        GenericFileNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "image",
                "document",
                "file",
                "download",
                "untitled",
                "new",
                "새 문서",
                "새 파일"
            };

    private static readonly string[] WorkKeywords =
    [
        "회의",
        "보고서",
        "프로젝트",
        "업무",
        "일정",
        "계약",
        "견적",
        "meeting",
        "report",
        "project",
        "proposal",
        "contract"
    ];

    private static readonly string[] StudyKeywords =
    [
        "공부",
        "과제",
        "강의",
        "수업",
        "시험",
        "논문",
        "학습",
        "study",
        "lecture",
        "assignment",
        "exam"
    ];

    private static readonly string[] FinanceKeywords =
    [
        "영수증",
        "청구서",
        "송장",
        "세금",
        "결제",
        "입금",
        "출금",
        "receipt",
        "invoice",
        "tax",
        "payment"
    ];

    private static readonly string[] PersonalKeywords =
    [
        "이력서",
        "자기소개서",
        "여권",
        "주민등록",
        "개인",
        "resume",
        "personal",
        "passport"
    ];

    public IReadOnlyList<FileClassification> Classify(
        IReadOnlyList<ScannedFile> files,
        string destinationRoot)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new ArgumentException(
                "기본 정리 폴더가 지정되지 않았습니다.",
                nameof(destinationRoot));
        }

        var duplicateNames = files
            .GroupBy(
                file => file.Name,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(
                StringComparer.OrdinalIgnoreCase);

        return files
            .Select(file => ClassifyFile(
                file,
                destinationRoot,
                duplicateNames))
            .ToArray();
    }

    private static FileClassification ClassifyFile(
        ScannedFile file,
        string destinationRoot,
        IReadOnlySet<string> duplicateNames)
    {
        if (duplicateNames.Contains(file.Name))
        {
            return CreateResult(
                file,
                destinationRoot,
                category: "분류 보류",
                confidence: ClassificationConfidence.Low,
                reason:
                    "동일한 파일명이 스캔 범위에 " +
                    "여러 개 존재함",
                moveRisk: MoveRiskLevel.Medium);
        }

        var fileNameWithoutExtension =
            Path.GetFileNameWithoutExtension(
                    file.Name)
                .Trim();

        if (GenericFileNames.Contains(
                fileNameWithoutExtension))
        {
            return CreateResult(
                file,
                destinationRoot,
                category: "분류 보류",
                confidence: ClassificationConfidence.Low,
                reason:
                    "파일명이 일반적이어서 용도를 " +
                    "안전하게 판단할 수 없음",
                moveRisk: MoveRiskLevel.Medium);
        }

        var extension =
            file.Extension.ToLowerInvariant();

        if (InstallerExtensions.Contains(extension))
        {
            return CreateResult(
                file,
                destinationRoot,
                category: "설치파일",
                confidence: ClassificationConfidence.High,
                reason:
                    "설치 패키지 확장자로 확인됨",
                moveRisk: MoveRiskLevel.High);
        }

        if (HighRiskDevelopmentExtensions.Contains(
                extension))
        {
            return CreateResult(
                file,
                destinationRoot,
                category: "개발 파일",
                confidence: ClassificationConfidence.High,
                reason:
                    "프로젝트 또는 프로그램 설정 파일임",
                moveRisk: MoveRiskLevel.High);
        }

        if (DevelopmentExtensions.Contains(extension))
        {
            return CreateResult(
                file,
                destinationRoot,
                category: "개발 파일",
                confidence: ClassificationConfidence.High,
                reason:
                    "소스코드 또는 개발 관련 확장자임",
                moveRisk: MoveRiskLevel.Medium);
        }

        if (TemporaryExtensions.Contains(extension))
        {
            return CreateResult(
                file,
                destinationRoot,
                category: "임시 파일",
                confidence: ClassificationConfidence.High,
                reason:
                    "임시 또는 백업 파일 확장자임",
                moveRisk: MoveRiskLevel.Medium);
        }

        if (DocumentExtensions.Contains(extension))
        {
            return ClassifyDocument(
                file,
                destinationRoot,
                fileNameWithoutExtension);
        }

        if (ImageExtensions.Contains(extension))
        {
            return ClassifyImage(
                file,
                destinationRoot,
                fileNameWithoutExtension);
        }

        if (ArchiveExtensions.Contains(extension))
        {
            return CreateResult(
                file,
                destinationRoot,
                category: "압축파일",
                confidence: ClassificationConfidence.High,
                reason:
                    "압축파일 확장자로 확인됨",
                moveRisk: MoveRiskLevel.Low);
        }

        if (MediaExtensions.Contains(extension))
        {
            return CreateResult(
                file,
                destinationRoot,
                category: "영상 및 음악",
                confidence: ClassificationConfidence.High,
                reason:
                    "영상 또는 음악 파일 확장자로 확인됨",
                moveRisk: MoveRiskLevel.Low);
        }

        return CreateResult(
            file,
            destinationRoot,
            category: "분류 보류",
            confidence: ClassificationConfidence.Low,
            reason:
                "지원하지 않는 확장자이거나 " +
                "분류 기준이 없음",
            moveRisk: MoveRiskLevel.Medium);
    }

    private static FileClassification ClassifyDocument(
        ScannedFile file,
        string destinationRoot,
        string fileName)
    {
        if (ContainsAny(fileName, WorkKeywords))
        {
            return CreateResult(
                file,
                destinationRoot,
                "문서/업무",
                ClassificationConfidence.High,
                "파일명에서 업무 관련 표현이 발견됨",
                MoveRiskLevel.Low);
        }

        if (ContainsAny(fileName, StudyKeywords))
        {
            return CreateResult(
                file,
                destinationRoot,
                "문서/공부",
                ClassificationConfidence.High,
                "파일명에서 공부 관련 표현이 발견됨",
                MoveRiskLevel.Low);
        }

        if (ContainsAny(fileName, FinanceKeywords))
        {
            return CreateResult(
                file,
                destinationRoot,
                "문서/금융 및 영수증",
                ClassificationConfidence.High,
                "파일명에서 금융 또는 영수증 관련 " +
                "표현이 발견됨",
                MoveRiskLevel.Low);
        }

        if (ContainsAny(fileName, PersonalKeywords))
        {
            return CreateResult(
                file,
                destinationRoot,
                "문서/개인 문서",
                ClassificationConfidence.High,
                "파일명에서 개인 문서 관련 표현이 발견됨",
                MoveRiskLevel.Low);
        }

        return CreateResult(
            file,
            destinationRoot,
            "문서/개인 문서",
            ClassificationConfidence.Medium,
            "문서 확장자로 확인됐지만 정확한 용도는 " +
            "판단할 수 없음",
            MoveRiskLevel.Low);
    }

    private static FileClassification ClassifyImage(
        ScannedFile file,
        string destinationRoot,
        string fileName)
    {
        if (ContainsAny(
                fileName,
                ["스크린샷", "screenshot", "캡처", "capture"]))
        {
            return CreateResult(
                file,
                destinationRoot,
                "사진/스크린샷",
                ClassificationConfidence.High,
                "파일명에서 스크린샷 관련 표현이 발견됨",
                MoveRiskLevel.Low);
        }

        if (ContainsAny(
                fileName,
                ["여행", "trip", "travel", "vacation"]))
        {
            return CreateResult(
                file,
                destinationRoot,
                "사진/여행",
                ClassificationConfidence.High,
                "파일명에서 여행 관련 표현이 발견됨",
                MoveRiskLevel.Low);
        }

        if (ContainsAny(
                fileName,
                ["영수증", "receipt", "invoice", "scan", "스캔"]))
        {
            return CreateResult(
                file,
                destinationRoot,
                "사진/영수증 및 문서 촬영",
                ClassificationConfidence.High,
                "파일명에서 영수증 또는 문서 촬영 " +
                "관련 표현이 발견됨",
                MoveRiskLevel.Low);
        }

        return CreateResult(
            file,
            destinationRoot,
            "사진/기타 사진",
            ClassificationConfidence.Medium,
            "이미지 확장자로 확인됐지만 정확한 용도는 " +
            "판단할 수 없음",
            MoveRiskLevel.Low);
    }

    private static FileClassification CreateResult(
        ScannedFile file,
        string destinationRoot,
        string category,
        ClassificationConfidence confidence,
        string reason,
        MoveRiskLevel moveRisk)
    {
        var relativeFolder =
            category.Replace(
                '/',
                Path.DirectorySeparatorChar);

        var suggestedFolder =
            Path.Combine(
                destinationRoot,
                relativeFolder);

        var requiresReview =
            confidence != ClassificationConfidence.High
            || moveRisk != MoveRiskLevel.Low;

        return new FileClassification(
            File: file,
            Category: category,
            SuggestedFolder: suggestedFolder,
            Confidence: confidence,
            Reason: reason,
            MoveRisk: moveRisk,
            RequiresReview: requiresReview);
    }

    private static bool ContainsAny(
        string value,
        IEnumerable<string> keywords)
    {
        return keywords.Any(keyword =>
            value.Contains(
                keyword,
                StringComparison.OrdinalIgnoreCase));
    }
}
