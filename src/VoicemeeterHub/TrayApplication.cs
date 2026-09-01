#if WINDOWS
using System.Diagnostics;
using System.Drawing;
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
        private int _exiting;

        public TrayContext(CancellationTokenSource shutdown, HubServer server, Task<int> serverTask, int port)
        {
            _shutdown = shutdown;
            _server = server;
            _serverTask = serverTask;
            _port = port;

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = BuildToolTip(port),
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };
            _notifyIcon.DoubleClick += (_, _) => ShowStatus();
            _dispatcher.EnsureHandle();

            _serverTask.ContinueWith(
                task => _dispatcher.BeginInvoke(() => OnServerStopped(task)),
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
            var logPath = Path.Combine(AppContext.BaseDirectory, "voicemeeter-hub.log");
            if (!File.Exists(logPath))
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
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
