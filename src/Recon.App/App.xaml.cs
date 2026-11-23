using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Recon.App.Platform.Windows;
using Forms = System.Windows.Forms;

namespace Recon.App;

public partial class App : System.Windows.Application
{
    private const string MutexName =
        @"Local\RECON.SingleInstance";

    private const string ShowWindowEventName =
        @"Local\RECON.ShowMainWindow";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    private EventWaitHandle? _showWindowEvent;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;

    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;

    private KeyboardHookService? _keyboardHookService;

    private bool _isExiting;

    protected override void OnStartup(
        StartupEventArgs e)
    {
        base.OnStartup(e);

        _showWindowEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ShowWindowEventName);

        _instanceMutex = new Mutex(
            initiallyOwned: true,
            name: MutexName,
            createdNew: out _ownsMutex);

        if (!_ownsMutex)
        {
            _showWindowEvent.Set();

            Shutdown();
            return;
        }

        StartSecondInstanceListener();
        InitializeTrayIcon();
        InitializeKeyboardHook();
    }

    private void InitializeKeyboardHook()
    {
        try
        {
            var desktopContextDetector =
                new DesktopContextDetector();

            _keyboardHookService =
                new KeyboardHookService(
                    desktopContextDetector);

            _keyboardHookService
                .DesktopCtrlFPressed +=
                OnDesktopCtrlFPressed;

            _keyboardHookService.Start();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"키보드 감지기 시작 실패: {exception}");

            if (_keyboardHookService is not null)
            {
                _keyboardHookService
                    .DesktopCtrlFPressed -=
                    OnDesktopCtrlFPressed;

                _keyboardHookService.Dispose();
                _keyboardHookService = null;
            }
        }
    }

    private void OnDesktopCtrlFPressed(
        object? sender,
        EventArgs e)
    {
        Dispatcher.BeginInvoke(
            new Action(ShowMainWindow));
    }

    private void StartSecondInstanceListener()
    {
        var showWindowEvent = _showWindowEvent
            ?? throw new InvalidOperationException(
                "창 표시 이벤트가 생성되지 않았습니다.");

        _listenerCancellation =
            new CancellationTokenSource();

        var cancellation =
            _listenerCancellation;

        _listenerTask = Task.Run(() =>
        {
            var waitHandles = new WaitHandle[]
            {
                showWindowEvent,
                cancellation.Token.WaitHandle
            };

            while (true)
            {
                var result =
                    WaitHandle.WaitAny(waitHandles);

                if (result != 0)
                {
                    break;
                }

                Dispatcher.BeginInvoke(
                    new Action(ShowMainWindow));
            }
        });
    }

    private void InitializeTrayIcon()
    {
        var openItem =
            new Forms.ToolStripMenuItem(
                "RECON 열기");

        openItem.Click += (_, _) =>
        {
            Dispatcher.BeginInvoke(
                new Action(ShowMainWindow));
        };

        var exitItem =
            new Forms.ToolStripMenuItem(
                "종료");

        exitItem.Click += (_, _) =>
        {
            Dispatcher.BeginInvoke(
                new Action(ExitApplication));
        };

        _trayMenu =
            new Forms.ContextMenuStrip();

        _trayMenu.Items.Add(openItem);

        _trayMenu.Items.Add(
            new Forms.ToolStripSeparator());

        _trayMenu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "RECON",
            Icon = System.Drawing
                .SystemIcons.Application,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) =>
        {
            Dispatcher.BeginInvoke(
                new Action(ShowMainWindow));
        };
    }

    private void ShowMainWindow()
    {
        if (_isExiting)
        {
            return;
        }

        if (MainWindow is null)
        {
            var window = new MainWindow();

            window.Closing +=
                OnMainWindowClosing;

            MainWindow = window;
        }

        if (!MainWindow.IsVisible)
        {
            MainWindow.Show();
        }

        if (MainWindow.WindowState ==
            WindowState.Minimized)
        {
            MainWindow.WindowState =
                WindowState.Normal;
        }

        MainWindow.Activate();

        MainWindow.Topmost = true;
        MainWindow.Topmost = false;

        MainWindow.Focus();
        
        if (MainWindow is Recon.App.MainWindow quickPanel)
        {
            quickPanel.FocusSearchBox();
        }
    }

    private void OnMainWindowClosing(
        object? sender,
        CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;

        if (sender is Window window)
        {
            window.Hide();
        }
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }

        MainWindow?.Close();

        Shutdown();
    }

    protected override void OnExit(
        ExitEventArgs e)
    {
        _isExiting = true;

        if (_keyboardHookService is not null)
        {
            _keyboardHookService
                .DesktopCtrlFPressed -=
                OnDesktopCtrlFPressed;

            _keyboardHookService.Dispose();
            _keyboardHookService = null;
        }

        _listenerCancellation?.Cancel();

        try
        {
            _listenerTask?.Wait(
                TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // 종료 중 작업 취소는 무시합니다.
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _trayMenu?.Dispose();

        _listenerCancellation?.Dispose();
        _showWindowEvent?.Dispose();

        if (_ownsMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 이미 해제된 경우 무시합니다.
            }
        }

        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
