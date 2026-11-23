using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Recon.App.Services;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfMessageBox = System.Windows.MessageBox;

namespace Recon.App;

public partial class MainWindow : Window
{
    private readonly FileScanService
        _fileScanService = new();

    private readonly RuleBasedFileClassifier
        _fileClassifier = new();

    private readonly SafeCommitService
        _safeCommitService = new();

    private readonly MovePreflightValidator
        _movePreflightValidator = new();

    private readonly FileHashService
        _fileHashService = new();

    private readonly SafeCommitStateService
        _safeCommitStateService = new();

    private readonly DestinationPolicyService
        _destinationPolicyService = new();

    private readonly DesktopIconSnapshotService
        _desktopIconSnapshotService = new();

    private readonly DesktopIconSnapshotVerifier
        _desktopIconSnapshotVerifier = new();

    private readonly DesktopIconIdentityMapperService
        _desktopIconIdentityMapperService = new();

    private readonly FileOperationDryRunService
        _fileOperationDryRunService = new();

    private readonly
    FileOperationDryRunPersistenceService
    _fileOperationDryRunPersistenceService =
        new();

    private bool _suppressAutoHide;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        FocusSearchBox();
    }

    public void FocusSearchBox()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                SearchBox.SelectAll();
            }));
    }

    private async void OrganizeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        using var folderDialog =
            new Forms.FolderBrowserDialog
            {
                Description =
                    "RECON으로 분석할 폴더를 선택하세요.",

                ShowNewFolderButton = false,

                SelectedPath =
                    Environment.GetFolderPath(
                        Environment.SpecialFolder
                            .DesktopDirectory)
            };

        Forms.DialogResult dialogResult;

        _suppressAutoHide = true;

        try
        {
            dialogResult =
                folderDialog.ShowDialog();
        }
        finally
        {
            _suppressAutoHide = false;
        }

        if (dialogResult != Forms.DialogResult.OK)
        {
            FocusSearchBox();
            return;
        }

        var button =
            sender as WpfButton;

        var originalContent =
            button?.Content;

        _suppressAutoHide = true;

        try
        {
            if (button is not null)
            {
                button.IsEnabled = false;
                button.Content = "분석 중...";
            }

            /*
             * 1. 선택한 폴더 스캔
             */
            var scanResult =
                await _fileScanService.ScanAsync(
                    folderDialog.SelectedPath,
                    CancellationToken.None);

            /*
             * 2. 안전한 로컬 정리 목적지 확인
             */
            var destinationRoot =
                _destinationPolicyService
                    .GetDefaultDestinationRoot();

            var destinationPolicy =
                _destinationPolicyService.Validate(
                    scanResult.RootPath,
                    destinationRoot);

            if (!destinationPolicy.IsAllowed)
            {
                throw new InvalidOperationException(
                    "안전한 정리 목적지를 " +
                    "사용할 수 없습니다.\n\n" +
                    destinationPolicy.Reason);
            }

            destinationRoot =
                destinationPolicy.DestinationRoot;

            /*
             * 3. 파일 분류
             */
            var classifications =
                _fileClassifier.Classify(
                    scanResult.Files,
                    destinationRoot);

            /*
             * 4. 정리 계획 미리보기
             */
            var previewWindow =
                new PlanPreviewWindow(
                    classifications,
                    scanResult.RootPath,
                    destinationRoot,
                    scanResult.SkippedFileCount,
                    scanResult.Errors.Count)
                {
                    Owner = this
                };

            var accepted =
                previewWindow.ShowDialog() == true;

            if (!accepted)
            {
                return;
            }

            /*
             * 5. Safe Commit 이름 생성
             */
            var sourceDirectoryName =
                new DirectoryInfo(
                    scanResult.RootPath).Name;

            if (string.IsNullOrWhiteSpace(
                    sourceDirectoryName))
            {
                sourceDirectoryName =
                    scanResult.RootPath;
            }

            var commitName =
                $"{sourceDirectoryName} 정리 계획";

            /*
             * 6. 사용자가 검토한 계획
             */
            var reviewedPlan =
                previewWindow.CreateSafeCommitPlan();

            /*
             * 7. 이동 전 검증
             */
            if (button is not null)
            {
                button.Content =
                    "이동 전 검증 중...";
            }

            var preflightResult =
                await _movePreflightValidator
                    .ValidateAsync(
                        reviewedPlan,
                        destinationRoot,
                        CancellationToken.None);

            /*
             * 8. 검증 결과 미리보기
             */
            var preflightWindow =
                new MovePreflightWindow(
                    preflightResult)
                {
                    Owner = this
                };

            var saveSafeCommit =
                preflightWindow.ShowDialog() == true;

            if (!saveSafeCommit)
            {
                return;
            }

            /*
             * 9. SHA-256 계산
             */
            if (button is not null)
            {
                button.Content =
                    "SHA-256 계산 중...";
            }

            var hashResult =
                await _fileHashService.ComputeAsync(
                    reviewedPlan,
                    preflightResult,
                    CancellationToken.None);

            reviewedPlan =
                hashResult.PlanItems;

            /*
             * 10. Draft Safe Commit 저장
             */
            if (button is not null)
            {
                button.Content =
                    "Safe Commit 저장 중...";
            }

            var safeCommit =
                await _safeCommitService
                    .CreateDraftAsync(
                        commitName:
                            commitName,

                        sourceFolder:
                            scanResult.RootPath,

                        destinationRoot:
                            destinationRoot,

                        planItems:
                            reviewedPlan,

                        preflightResult:
                            preflightResult,

                        skippedFileCount:
                            scanResult
                                .SkippedFileCount,

                        scanErrorCount:
                            scanResult.Errors.Count,

                        cancellationToken:
                            CancellationToken.None);

            /*
             * 11. 최초 Safe Commit 상태 평가
             */
            var stateResult =
                await _safeCommitStateService
                    .EvaluateDraftAsync(
                        safeCommit.FilePath,
                        CancellationToken.None);

            /*
             * 12. 바탕화면 아이콘 좌표 저장
             */
            if (button is not null)
            {
                button.Content =
                    "아이콘 좌표 저장 중...";
            }

            var iconSnapshotResult =
                await _desktopIconSnapshotService
                    .CaptureAsync(
                        safeCommit.FilePath,
                        CancellationToken.None);

            DesktopIconReadbackVerificationResult?
                iconReadbackResult = null;

            DesktopIconIdentityMappingResult?
                iconIdentityResult = null;

            /*
             * 13. 저장한 아이콘 좌표 재조회 검증
             */
            if (iconSnapshotResult.IsSuccessful)
            {
                if (button is not null)
                {
                    button.Content =
                        "아이콘 좌표 검증 중...";
                }

                iconReadbackResult =
                    await _desktopIconSnapshotVerifier
                        .VerifyAsync(
                            safeCommit.FilePath,
                            CancellationToken.None);

                /*
                 * 14. 아이콘을 실제 파일·폴더와 연결
                 */
                if (iconReadbackResult.IsSuccessful)
                {
                    iconIdentityResult =
                        await _desktopIconIdentityMapperService
                            .MapAsync(
                                safeCommit.FilePath,
                                CancellationToken.None);
                }

                /*
                 * 15. 아이콘 조건 반영 후 상태 재평가
                 */
                stateResult =
                    await _safeCommitStateService
                        .EvaluateDraftAsync(
                            safeCommit.FilePath,
                            CancellationToken.None);
            }

            /*
             * 16. 최종 파일 이동 드라이런
             * 실제 File.Move는 호출하지 않습니다.
             */
            FileOperationDryRunResult?
                dryRunResult = null;

            FileOperationDryRunPersistenceResult?
    dryRunPersistenceResult = null;

        if (stateResult.CanExecute)
        {
            if (button is not null)
            {
                button.Content =
                    "최종 드라이런 중...";
            }

            dryRunResult =
                await _fileOperationDryRunService
                    .RunAsync(
                        safeCommit.FilePath,
                        CancellationToken.None);

            if (button is not null)
            {
                button.Content =
                    "드라이런 결과 저장 중...";
            }

            dryRunPersistenceResult =
                await _fileOperationDryRunPersistenceService
                    .PersistAsync(
                        safeCommit.FilePath,
                        dryRunResult,
                        CancellationToken.None);

            if (dryRunPersistenceResult.IsSuccessful)
            {
                stateResult =
                    stateResult with
                    {
                        PreviousStatus =
                            stateResult.CurrentStatus,

                        CurrentStatus =
                            dryRunPersistenceResult
                                .CurrentStatus,

                        CanExecute =
                            dryRunPersistenceResult
                                .CanExecute,

                        Reason =
                            dryRunPersistenceResult
                                .Reason
                    };
            }
        }


            /*
             * Safe Commit 상태와 드라이런이 모두
             * 통과해야 최종 실행 가능 상태입니다.
             */
            var finalExecutionReady =
                stateResult.CanExecute
                &&
                dryRunResult?.IsSuccessful == true
                &&
                dryRunPersistenceResult
                    ?.DryRunPassed == true;

            /*
             * 17. 최종 결과 표시
             */
        WpfMessageBox.Show(
            this,
            $"""
            파일 분석이 완료되었습니다.

            Safe Commit ID:
            {safeCommit.CommitId}

            분석한 폴더:
            {scanResult.RootPath}

            추천 정리 위치:
            {destinationRoot}

            사용자 승인:
            {safeCommit.ApprovedFileCount:N0}개

            이동 가능 후보:
            {safeCommit.ReadyFileCount:N0}개

            사용자 제외:
            {safeCommit.ExcludedFileCount:N0}개

            안전 차단:
            {safeCommit.BlockedFileCount:N0}개

            SHA-256 계산 완료:
            {hashResult.ComputedCount:N0}개

            SHA-256 생략:
            {hashResult.SkippedCount:N0}개

            SHA-256 실패:
            {hashResult.FailedCount:N0}개

            아이콘 좌표 저장:
            {(iconSnapshotResult.IsSuccessful
                ? $"{iconSnapshotResult.IconCount:N0}개"
                : "실패")}

            아이콘 좌표 상태:
            {iconSnapshotResult.Reason}

            아이콘 좌표 재검증:
            {(iconReadbackResult is null
                ? "실행 안 함"
                : iconReadbackResult.Reason)}

            좌표 일치:
            {(iconReadbackResult?.MatchedCount ?? 0):N0}개

            좌표 변경:
            {(iconReadbackResult?.MovedCount ?? 0):N0}개

            아이콘 누락:
            {(iconReadbackResult?.MissingCount ?? 0):N0}개

            새 아이콘:
            {(iconReadbackResult?.ExtraCount ?? 0):N0}개

            아이콘 식별:
            {(iconIdentityResult is null
                ? "실행 안 함"
                : iconIdentityResult.Reason)}

            실제 경로 연결:
            {(iconIdentityResult?.MappedCount ?? 0):N0}개

            보호 대상:
            {(iconIdentityResult?.ProtectedCount ?? 0):N0}개

            일반 파일:
            {(iconIdentityResult?.MovableCount ?? 0):N0}개

            모호한 연결:
            {(iconIdentityResult?.AmbiguousCount ?? 0):N0}개

            미연결:
            {(iconIdentityResult?.UnmappedCount ?? 0):N0}개

            Safe Commit 상태:
            {stateResult.DisplayStatus}

            상태 사유:
            {stateResult.Reason}

            최종 안전 검증:
            {(dryRunResult is null
                ? "실행 안 함"
                : dryRunResult.Reason)}

            검증 통과:
            {(dryRunResult?.ReadyCount ?? 0):N0}개

            검증 제외:
            {(dryRunResult?.ExcludedCount ?? 0):N0}개

            검증 차단:
            {(dryRunResult?.BlockedCount ?? 0):N0}개

            안전 검증 결과:
            {(finalExecutionReady
                ? "통과"
                : "검토 필요")}

            기록 저장 위치:
            {safeCommit.FilePath}

            안내:
            이번 분석에서는 실제 파일을 이동하지 않았습니다.
            """,
            "RECON 분석 결과",
            MessageBoxButton.OK,
            finalExecutionReady
                ? MessageBoxImage.Information
                : MessageBoxImage.Warning);

        }
        catch (OperationCanceledException)
        {
            WpfMessageBox.Show(
                this,
                "파일 분석 작업이 취소되었습니다.",
                "RECON",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(
                this,
                "파일 분석 또는 Safe Commit 생성에 " +
                $"실패했습니다.\n\n{exception.Message}",
                "RECON 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _suppressAutoHide = false;

            if (button is not null)
            {
                button.IsEnabled = true;
                button.Content = originalContent;
            }

            FocusSearchBox();
        }
    }

    private void HistoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _suppressAutoHide = true;

        try
        {
            var historyWindow =
                new SafeCommitHistoryWindow
                {
                    Owner = this
                };

            historyWindow.ShowDialog();
        }
        finally
        {
            _suppressAutoHide = false;
            FocusSearchBox();
        }
    }

    private void IconDiagnosticButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _suppressAutoHide = true;

        try
        {
            var probeWindow =
                new DesktopIconProbeWindow
                {
                    Owner = this
                };

            probeWindow.ShowDialog();
        }
        finally
        {
            _suppressAutoHide = false;
            FocusSearchBox();
        }
    }

    private void Window_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        Hide();

        e.Handled = true;
    }

    private void Window_Deactivated(
        object? sender,
        EventArgs e)
    {
        if (_suppressAutoHide)
        {
            return;
        }

        if (IsVisible)
        {
            Hide();
        }
    }

    private void CloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Hide();
    }

    private void Header_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.LeftButton !=
            MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
    }
}
