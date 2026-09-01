#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using log4net;

namespace VoicemeeterHub;

internal static class TrayApplication
{
    public static void Run(CancellationTokenSource shutdown, HubServer server, Task<int> serverTask, int port)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext(shutdown, server, serverTask, port));
    }

    public static void ShowAlreadyRunningMessage()
    {
        MessageBox.Show(
            "Voicemeeter Hub is already running.",
            "Voicemeeter Hub",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    public static void ShowStartupError(Exception exception)
    {
        MessageBox.Show(
            $"Voicemeeter Hub failed to start.\n\n{exception.Message}",
            "Voicemeeter Hub",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private sealed class TrayContext : ApplicationContext
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(TrayContext));

        private readonly CancellationTokenSource _shutdown;
        private readonly HubServer _server;
        private readonly Task<int> _serverTask;
        private readonly int _port;
        private readonly DispatcherControl _dispatcher = new();
        private readonly NotifyIcon _notifyIcon;
        private readonly Icon _trayIcon;
        private int _exiting;

        public TrayContext(CancellationTokenSource shutdown, HubServer server, Task<int> serverTask, int port)
        {
            _shutdown = shutdown;
            _server = server;
            _serverTask = serverTask;
            _port = port;

            _trayIcon = CreateTrayIcon();
            _notifyIcon = new NotifyIcon
            {
                Icon = _trayIcon,
                Text = BuildToolTip(port),
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };
            _notifyIcon.DoubleClick += (_, _) => ShowStatus();
            _dispatcher.EnsureHandle();

            _serverTask.ContinueWith(
                task => TryDispatch(() => OnServerStopped(task)),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Status", null, (_, _) => ShowStatus());
            menu.Items.Add("Open Log", null, (_, _) => OpenLog());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApplication());
            return menu;
        }

        private void ShowStatus()
        {
            var message =
                $"Port: {_port}\n" +
                $"Connected clients: {_server.ConnectionCount}\n" +
                $"Protocol: v{HubProtocol.Version}\n" +
                $"Version: {_server.ServerVersion ?? "unknown"}";

            _notifyIcon.ShowBalloonTip(5000, "Voicemeeter Hub", message, ToolTipIcon.Info);
        }

        private static string BuildToolTip(int port)
        {
            var text = $"Voicemeeter Hub (:{port})";
            return text.Length <= 63 ? text : text[..63];
        }

        private void OpenLog()
        {
            var logPath = ResolveLogPath();
            if (logPath == null || !File.Exists(logPath))
            {
                MessageBox.Show(
                    "The log file has not been created yet.",
                    "Voicemeeter Hub",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to open log file: {ex.Message}");
                MessageBox.Show(
                    $"Could not open the log file.\n\n{ex.Message}",
                    "Voicemeeter Hub",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void ExitApplication()
        {
            if (Interlocked.Exchange(ref _exiting, 1) != 0) return;

            _shutdown.Cancel();
            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException ex)
            {
                Log.Warn("Server task ended with an error during tray shutdown.", ex.Flatten());
            }

            ExitThread();
        }

        private void OnServerStopped(Task<int> task)
        {
            if (Interlocked.Exchange(ref _exiting, 1) != 0) return;

            if (task.IsFaulted && task.Exception != null)
                Log.Error("Voicemeeter hub server stopped unexpectedly.", task.Exception.Flatten());
            else if (!task.IsCanceled)
                Log.Info("Voicemeeter hub server stopped.");

            ExitThread();
        }

        /// <summary>
        ///     Marshals <paramref name="action"/> onto the UI thread, tolerating the shutdown race:
        ///     the continuation can fire after the context (and dispatcher handle) is already gone,
        ///     which would otherwise throw an unobserved exception on the thread pool.
        /// </summary>
        private void TryDispatch(Action action)
        {
            if (Volatile.Read(ref _exiting) != 0) return;
            try
            {
                if (_dispatcher.IsHandleCreated)
                    _dispatcher.BeginInvoke(action);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>Resolves the active log file from the log4net configuration, so the menu never
        /// hard-codes a name that can drift from <c>log4net.config</c>.</summary>
        private static string? ResolveLogPath()
        {
            var repository = LogManager.GetRepository(Assembly.GetExecutingAssembly());
            foreach (var appender in repository.GetAppenders())
                if (appender is log4net.Appender.FileAppender fileAppender && !string.IsNullOrEmpty(fileAppender.File))
                    return fileAppender.File;
            return null;
        }

        private static Icon CreateTrayIcon()
        {
            using var bitmap = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using var background = new SolidBrush(Color.FromArgb(0x1E, 0x88, 0xE5));
                graphics.FillEllipse(background, 1, 1, 30, 30);
                using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
                using var foreground = new SolidBrush(Color.White);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                graphics.DrawString("V", font, foreground, new RectangleF(0, 0, 32, 32), format);
            }

            // GetHicon hands back a native icon handle we must free; clone into a managed Icon that
            // owns its own handle so it survives the DestroyIcon below and is disposable normally.
            var hicon = bitmap.GetHicon();
            try
            {
                using var native = Icon.FromHandle(hicon);
                return (Icon)native.Clone();
            }
            finally
            {
                DestroyIcon(hicon);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.ContextMenuStrip?.Dispose();
                _notifyIcon.Dispose();
                _trayIcon.Dispose();
                _dispatcher.Dispose();
            }

            base.Dispose(disposing);
        }

        private sealed class DispatcherControl : Control
        {
            public void EnsureHandle() => CreateHandle();
        }
    }
}
#endif
