using System.Reflection;
using log4net;
using log4net.Config;
using VoicemeeterHub;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
ConfigureLogging();
var log = LogManager.GetLogger("VoicemeeterHub.Program");

// Single-instance guard: the DLL Remote API allows one login session per machine, so only one
// hub may own it. A second launch just exits — the first instance keeps serving every client.
using var mutex = new Mutex(true, HubProtocol.MutexName, out var createdNew);
if (!createdNew && !mutex.WaitOne(TimeSpan.Zero))
{
    log.Info("Another Voicemeeter hub instance is already running; exiting.");
    return 0;
}

var port = ResolvePort(args);
using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

using var server = new HubServer();
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
    log.Error("Voicemeeter hub terminated with an unhandled exception.", ex);
    var logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
    await File.AppendAllTextAsync(logPath, $"[{DateTimeOffset.Now:O}] {ex}\n");
    return 1;
}

static int ResolvePort(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var cli) && cli is > 0 and < 65536)
            return cli;
    return HubProtocol.ResolvePort();
}

static void ConfigureLogging()
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
