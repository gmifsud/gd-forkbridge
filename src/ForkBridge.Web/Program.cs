using System.Net;
using System.Security.Cryptography;

namespace ForkBridge.Web;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? config = null; int port = 5178;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": config = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
                case "--urls" or "--bind" or "--host":
                    Console.Error.WriteLine("ForkBridge web only listens on 127.0.0.1. Non-loopback binding is not supported.");
                    return 14;
                case "--help" or "-h":
                    Console.WriteLine("forkbridge-web --config <forkbridge.yml> [--port 5178]\nServes the local UI on http://127.0.0.1:<port>/ (loopback only).");
                    return 0;
            }
        }
        config ??= Environment.GetEnvironmentVariable("FORKBRIDGE_CONFIG") ?? "forkbridge.yml";
        // Which binary is serving: several copies (bin/Debug, bin/Release, a published folder behind a PATH shim)
        // can exist, and a stale one silently produces stale analysis.
        var core = typeof(ForkBridge.Core.ForkBridgeEngine).Assembly.Location;
        Console.WriteLine($"ForkBridge engine: {core} (built {File.GetLastWriteTime(core):yyyy-MM-dd HH:mm:ss})");
        Console.WriteLine($"Config: {Path.GetFullPath(config)} (working directory: {Environment.CurrentDirectory})");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var app = WebHost.Build(new WebOptions { ConfigPath = config, Port = port, Token = token });
        await app.StartAsync();
        Console.WriteLine($"ForkBridge UI (loopback only): http://127.0.0.1:{port}/#token={token}");
        Console.WriteLine("The token is required for every API call; it is valid until this process exits. Ctrl+C to stop.");
        await app.WaitForShutdownAsync();
        return 0;
    }
}
