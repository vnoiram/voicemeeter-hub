using System.Reflection;
using log4net;
using log4net.Config;

namespace VoicemeeterHub;

public static class Program
{
    private static readonly ILog Log = LogManager.GetLogger("VoicemeeterHub.Program");

    [STAThread]
    public static int Main(string[] args)
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        ConfigureLogging();

        using var mutex = new Mutex(true, HubProtocol.MutexName, out var createdNew);
        if (!createdNew && !mutex.WaitOne(TimeSpan.Zero))
        {
            Log.Info("Another Voicemeeter hub instance is already running; exiting.");
#if WINDOWS
            TrayApplication.ShowAlreadyRunningMessage();
#endif
            return 0;
        }

        var port = ResolvePort(args);
#if WINDOWS
        return RunTray(port);
#else
        return RunConsoleAsync(port).GetAwaiter().GetResult();
#endif
    }

#if WINDOWS
    private static int RunTray(int port)
    {
        using var shutdown = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
        Microsoft.Win32.SessionEndingEventHandler sessionEnding = (_, _) => shutdown.Cancel();
        Microsoft.Win32.SystemEvents.SessionEnding += sessionEnding;

        using var server = new HubServer();
        var serverTask = server.RunAsync(port, shutdown.Token);
        try
        {
            var listeningTask = server.Listening;
            var startupTask = Task.WhenAny(listeningTask, serverTask).GetAwaiter().GetResult();
            if (startupTask == serverTask)
            {
                if (listeningTask.IsFaulted)
                    listeningTask.GetAwaiter().GetResult();
                return serverTask.GetAwaiter().GetResult();
            }

            var boundPort = listeningTask.GetAwaiter().GetResult();
            TrayApplication.Run(shutdown, server, serverTask, boundPort);
            return WaitForServerExit(serverTask);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            LogStartupException(ex);
            TrayApplication.ShowStartupError(ex);
            shutdown.Cancel();
            return 1;
        }
        finally
        {
            Microsoft.Win32.SystemEvents.SessionEnding -= sessionEnding;
        }
    }

    private static int WaitForServerExit(Task<int> serverTask)
    {
        try
        {
            return serverTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            LogStartupException(ex);
            return 1;
        }
    }
#else
    private static async Task<int> RunConsoleAsync(int port)
    {
        using var shutdown = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

        using var server = new HubServer(idleTimeout: HubServer.DefaultIdleTimeout);
        try
        {
            return await server.RunAsync(port, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            LogStartupException(ex);
            return 1;
        }
    }
#endif

    private static int ResolvePort(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var cli) && cli is > 0 and < 65536)
                return cli;
        return HubProtocol.ResolvePort();
    }

    private static void ConfigureLogging()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "log4net.config");
            var repository = LogManager.GetRepository(Assembly.GetExecutingAssembly());
            if (File.Exists(configPath))
                XmlConfigurator.Configure(repository, new FileInfo(configPath));
            else
                BasicConfigurator.Configure(repository);
        }
        catch
        {
            // Logging must never prevent startup.
        }
    }

    private static void LogStartupException(Exception ex)
    {
        Log.Error("Voicemeeter hub terminated with an unhandled exception.", ex);
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] {ex}\n");
        }
        catch
        {
            // Best-effort fallback for failures before log4net is fully usable.
        }
    }
}
