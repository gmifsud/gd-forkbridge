using System.Net;
using System.Security.Cryptography;
using ForkBridge.Core.Git;

namespace ForkBridge.Web;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? config = null; int port = 5178; string? explicitToken = null;
        bool dev = string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase)
                   || Environment.GetEnvironmentVariable("FORKBRIDGE_DEV") == "1"
                   || Environment.GetEnvironmentVariable("FORKBRIDGE_VERBOSE") == "1";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": config = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
                case "--token": explicitToken = args[++i]; break;
                case "--dev" or "--verbose" or "-v": dev = true; break;
                case "--urls" or "--bind" or "--host":
                    Console.Error.WriteLine("ForkBridge web only listens on 127.0.0.1. Non-loopback binding is not supported.");
                    return 14;
                case "--help" or "-h":
                    Console.WriteLine("forkbridge-web [--config <forkbridge.yml>] [--port 5178] [--dev] [--token <hex>]\nServes the local UI on http://127.0.0.1:<port>/ (loopback only).");
                    return 0;
            }
        }
        config ??= Environment.GetEnvironmentVariable("FORKBRIDGE_CONFIG")
            ?? (File.Exists("forkbridge.yml") ? "forkbridge.yml"
                : File.Exists(Path.Combine(AppContext.BaseDirectory, "forkbridge.yml")) ? Path.Combine(AppContext.BaseDirectory, "forkbridge.yml")
                : "forkbridge.yml");

        if (dev) GitClient.VerboseLogging = true;

        // Which binary is serving: several copies (bin/Debug, bin/Release, a published folder behind a PATH shim)
        // can exist, and a stale one silently produces stale analysis.
        var core = typeof(ForkBridge.Core.ForkBridgeEngine).Assembly.Location;
        Console.WriteLine($"ForkBridge engine: {core} (built {File.GetLastWriteTime(core):yyyy-MM-dd HH:mm:ss})");
        Console.WriteLine($"Config: {Path.GetFullPath(config)} (working directory: {Environment.CurrentDirectory})");
        Console.WriteLine($"Mode:   {(dev ? "DEVELOPMENT (verbose HTTP + job + git logging enabled)" : "standard")}");

        var token = explicitToken
            ?? Environment.GetEnvironmentVariable("FORKBRIDGE_TOKEN")
            ?? (dev ? "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());

        var app = WebHost.Build(new WebOptions { ConfigPath = config, Port = port, Token = token, Verbose = dev });
        await app.StartAsync();
        Console.WriteLine($"ForkBridge UI (built static):   http://127.0.0.1:{port}/#token={token}");
        if (dev)
            Console.WriteLine($"Angular Dev Server (ng serve):  http://localhost:4300/#token={token}");
        Console.WriteLine("The token is required for every API call; it is valid until this process exits. Ctrl+C to stop.");
        await app.WaitForShutdownAsync();
        return 0;
    }
}
