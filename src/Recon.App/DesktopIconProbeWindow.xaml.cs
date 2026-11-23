using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Recon.App.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace Recon.App;

public partial class DesktopIconProbeWindow :
    System.Windows.Window
{
    private readonly DesktopIconProbeService
        _probeService = new();

    private readonly DesktopIconRoundTripTestService
        _roundTripTestService = new();

    private readonly DesktopIconCapabilityStore
        _capabilityStore = new();

    public DesktopIconProbeWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void RefreshButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void RoundTripTestButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var confirmation =
            WpfMessageBox.Show(
                this,
                """
                RECON_ICON_POSITION_TEST.txt 파일을
                바탕화면에 생성하거나 기존 테스트
                파일을 사용합니다.

                해당 테스트 아이콘 하나를 잠깐 이동한 뒤
                즉시 원래 위치로 복구합니다.

                기존 사용자 아이콘은 테스트 대상으로
                사용하지 않습니다.

                계속하시겠습니까?
                """,
                "RECON 아이콘 이동·복원 테스트",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

        if (confirmation !=
            MessageBoxResult.Yes)
        {
            return;
        }

        RefreshButton.IsEnabled = false;
        RoundTripTestButton.IsEnabled = false;

        StatusText.Text =
            "테스트 아이콘 이동 및 복원을 " +
            "검증하는 중입니다.";

        try
        {
            var result =
                await _roundTripTestService
                    .RunAsync(
                        CancellationToken.None);

            var capabilityResult =
                await _capabilityStore.SaveAsync(
                    result,
                    CancellationToken.None);

            await RefreshAsync();

            WpfMessageBox.Show(
                this,
                $"""
                테스트 결과:
                {(result.IsSuccessful
                    ? "성공"
                    : "실패")}

                사유:
                {result.Reason}

                원래 좌표:
                X {result.OriginalX:N0}
                Y {result.OriginalY:N0}

                이동 좌표:
                X {result.MovedX:N0}
                Y {result.MovedY:N0}

                복원 좌표:
                X {result.RestoredX:N0}
                Y {result.RestoredY:N0}

                기능 검증 기록:
                {(capabilityResult.IsVerified
                    ? "검증 완료"
                    : "검증 미완료")}

                기록 사유:
                {capabilityResult.Reason}

                기록 위치:
                {capabilityResult.FilePath ??
                    "저장되지 않음"}

                테스트 파일:
                {result.TestFilePath}
                """,
                "RECON 아이콘 복원 테스트",
                MessageBoxButton.OK,
                result.IsSuccessful
                && capabilityResult.IsVerified
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            WpfMessageBox.Show(
                this,
                "아이콘 이동·복원 테스트가 " +
                "취소됐습니다.",
                "RECON",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(
                this,
                "아이콘 이동·복원 테스트 중 " +
                $"오류가 발생했습니다.\n\n" +
                exception.Message,
                "RECON 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RoundTripTestButton.IsEnabled = true;
        }
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;

        StatusText.Text =
            "바탕화면 아이콘 목록을 읽는 중입니다.";

        HandleText.Text = "";

        try
        {
            var result =
                await Task.Run(
                    _probeService.Probe);

            IconGrid.ItemsSource =
                result.Icons;

            StatusText.Text =
                result.IsSuccessful
                    ? $"{result.Reason} " +
                      $"총 {result.Icons.Count:N0}개"
                    : result.Reason;

            HandleText.Text =
                result.ListViewHandle ==
                IntPtr.Zero
                    ? "바탕화면 목록 핸들 없음"
                    : $"ListView Handle: 0x" +
                      $"{result.ListViewHandle
                          .ToInt64():X}";
        }
        catch (Exception exception)
        {
            IconGrid.ItemsSource =
                Array.Empty<
                    DesktopIconProbeItem>();

            StatusText.Text =
                $"진단 실패: {exception.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }
}
