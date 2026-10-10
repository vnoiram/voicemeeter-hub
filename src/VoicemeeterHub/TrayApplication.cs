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
        private readonly HubSettings _settings = HubSettings.Load();
        private SettingsForm? _settingsForm;
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
            _dispatcher.HotkeyPressed += RestartAudioEngine;
            if (_settings.RestartHotkey is { } hotkey && !_dispatcher.TryRegisterHotkey(hotkey))
                Log.Warn($"Could not register restart hotkey {hotkey}; it may be in use by another application.");

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
            menu.Items.Add("Restart Audio Engine", null, (_, _) => RestartAudioEngine());
            menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
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

        private void RestartAudioEngine()
        {
            if (Volatile.Read(ref _exiting) != 0) return;

            _ = Task.Run(async () =>
            {
                VoicemeeterOperationResult result;
                try
                {
                    result = await _server.Client.TriggerCommandAsync("Restart");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Audio engine restart failed: {ex.Message}");
                    result = VoicemeeterOperationResult.Fail(-1, "Command.Restart", ex.Message);
                }

                TryDispatch(() => _notifyIcon.ShowBalloonTip(
                    3000,
                    "Voicemeeter Hub",
                    result.Success ? "Audio engine restart requested." : $"Restart failed: {result.ErrorSummary}",
                    result.Success ? ToolTipIcon.Info : ToolTipIcon.Warning));
            });
        }

        private void ShowSettings()
        {
            if (_settingsForm is { IsDisposed: false })
            {
                _settingsForm.Activate();
                return;
            }

            var form = new SettingsForm(_settings.RestartHotkey, ApplyRestartHotkey);
            form.FormClosed += (_, _) =>
            {
                form.Dispose();
                if (ReferenceEquals(_settingsForm, form)) _settingsForm = null;
            };
            _settingsForm = form;
            form.Show();
        }

        /// <summary>Registers <paramref name="hotkey"/> and persists it; keeps the previous binding on failure.</summary>
        private bool ApplyRestartHotkey(HotkeyBinding? hotkey)
        {
            var previous = _settings.RestartHotkey;
            _dispatcher.UnregisterHotkey();
            if (hotkey != null && !_dispatcher.TryRegisterHotkey(hotkey))
            {
                if (previous != null) _dispatcher.TryRegisterHotkey(previous);
                return false;
            }

            _settings.RestartHotkey = hotkey;
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to save settings: {ex.Message}");
                MessageBox.Show(
                    $"The hotkey is active but could not be saved.\n\n{ex.Message}",
                    "Voicemeeter Hub",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            return true;
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
                _settingsForm?.Dispose();
                _dispatcher.UnregisterHotkey();
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
            private const int WmHotkey = 0x0312;
            private const int ModNoRepeat = 0x4000;
            private const int HotkeyId = 1;
            private bool _registered;

            public event Action? HotkeyPressed;

            public void EnsureHandle() => CreateHandle();

            public bool TryRegisterHotkey(HotkeyBinding binding)
            {
                UnregisterHotkey();
                if (!IsHandleCreated) return false;
                _registered = RegisterHotKey(Handle, HotkeyId, (int)binding.Modifiers | ModNoRepeat, binding.VirtualKey);
                return _registered;
            }

            public void UnregisterHotkey()
            {
                if (!_registered) return;
                if (IsHandleCreated) UnregisterHotKey(Handle, HotkeyId);
                _registered = false;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmHotkey && m.WParam == (IntPtr)HotkeyId)
                    HotkeyPressed?.Invoke();
                else
                    base.WndProc(ref m);
            }

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        }
    }

    private sealed class SettingsForm : Form
    {
        private readonly Func<HotkeyBinding?, bool> _apply;
        private readonly CheckBox _enabled = new() { Text = "Enable restart hotkey", AutoSize = true };
        private readonly TextBox _capture = new() { ReadOnly = true, Width = 220, ShortcutsEnabled = false };
        private readonly Label _hint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
        private HotkeyBinding? _binding;

        public SettingsForm(HotkeyBinding? current, Func<HotkeyBinding?, bool> apply)
        {
            _apply = apply;
            _binding = current;

            Text = "Voicemeeter Hub Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.None };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var clear = new Button { Text = "Clear", AutoSize = true };
            AcceptButton = ok;
            CancelButton = cancel;

            var label = new Label { Text = "Restart audio engine:", AutoSize = true, Anchor = AnchorStyles.Left };
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { cancel, ok });

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, RowCount = 4 };
            layout.Controls.Add(_enabled, 0, 0);
            layout.SetColumnSpan(_enabled, 3);
            layout.Controls.Add(label, 0, 1);
            layout.Controls.Add(_capture, 1, 1);
            layout.Controls.Add(clear, 2, 1);
            layout.Controls.Add(_hint, 0, 2);
            layout.SetColumnSpan(_hint, 3);
            layout.Controls.Add(buttons, 0, 3);
            layout.SetColumnSpan(buttons, 3);
            Controls.Add(layout);

            _hint.Text = "Click the box and press a shortcut (Ctrl/Alt/Win + A-Z, 0-9 or F1-F24).";
            _enabled.Checked = current != null;
            UpdateView();

            _capture.KeyDown += OnCaptureKeyDown;
            _capture.KeyPress += (_, e) => e.Handled = true;
            clear.Click += (_, _) =>
            {
                _binding = null;
                UpdateView();
            };
            _enabled.CheckedChanged += (_, _) => UpdateView();
            ok.Click += (_, _) => Commit();
        }

        private void UpdateView()
        {
            _capture.Enabled = _enabled.Checked;
            _capture.Text = _binding?.ToString() ?? string.Empty;
        }

        private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            var modifiers = HotkeyModifiers.None;
            if (e.Control) modifiers |= HotkeyModifiers.Control;
            if (e.Alt) modifiers |= HotkeyModifiers.Alt;
            if (e.Shift) modifiers |= HotkeyModifiers.Shift;
            if (IsKeyDown(0x5B) || IsKeyDown(0x5C)) modifiers |= HotkeyModifiers.Win;

            var key = (int)e.KeyCode;
            var candidate = new HotkeyBinding(modifiers, key);
            if (!candidate.IsValid) return;

            _binding = candidate;
            UpdateView();
        }

        private void Commit()
        {
            var binding = _enabled.Checked ? _binding : null;
            if (_enabled.Checked && binding == null)
            {
                MessageBox.Show(this, "Press a shortcut in the box, or untick the option.", "Voicemeeter Hub",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!_apply(binding))
            {
                MessageBox.Show(this, $"{binding} is already in use by another application. Choose a different shortcut.",
                    "Voicemeeter Hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Close();
        }

        private static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int virtualKey);
    }
}
#endif
