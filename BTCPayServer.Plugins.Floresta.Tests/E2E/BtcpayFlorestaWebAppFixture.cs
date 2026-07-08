using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests.E2E;

public sealed class BtcpayFlorestaWebAppFixture : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "btcpay-floresta-e2e-" + Guid.NewGuid().ToString("N"));
    private Process? _process;
    private readonly StringBuilder _output = new();
    private string _btcpayProject = null!;
    private string _pluginDll = null!;
    private string _postgres = null!;
    private int _port;

    public Uri ServerUri { get; private set; } = null!;
    public string Output => _output.ToString();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _port = GetFreePort();
        ServerUri = new Uri($"http://127.0.0.1:{_port}/");

        _btcpayProject = Path.GetFullPath(
            Environment.GetEnvironmentVariable("BTCPAYSERVER_PROJECT") ??
            Path.Combine(GetRepoRoot(), "submodules", "btcpayserver", "BTCPayServer", "BTCPayServer.csproj"));
        if (!File.Exists(_btcpayProject))
            throw new InvalidOperationException($"BTCPayServer project not found at {_btcpayProject}");

        _pluginDll = typeof(FlorestaPlugin).Assembly.Location;
        if (!File.Exists(_pluginDll))
            throw new InvalidOperationException($"Floresta plugin assembly not found at {_pluginDll}");

        _postgres = Environment.GetEnvironmentVariable("E2E_POSTGRES") ??
                    "User ID=postgres;Password=postgres;Include Error Detail=true;Host=127.0.0.1;Port=39372;Database=btcpayserver_floresta_e2e";

        await StartProcessAsync();
    }

    public async Task RestartAsync()
    {
        await StopProcessAsync();
        await StartProcessAsync();
    }

    private async Task StartProcessAsync()
    {
        var args = new[]
        {
            "run",
            "--no-build",
            "--project",
            _btcpayProject,
            "--",
            "--datadir",
            _dataDir,
            "--network",
            "regtest",
            "--chains",
            "BTC",
            "--postgres",
            _postgres,
            "--port",
            _port.ToString(),
            "--bind",
            "127.0.0.1",
            "--nocsp=true",
            "--disable-registration",
            "false",
            "--cheatmode=true",
            "--debuglog",
            "debug.log"
        };

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = GetRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["BTCPAY_DEBUG_PLUGINS"] = _pluginDll;
        psi.Environment["DEBUG_PLUGINS"] = _pluginDll;
        psi.Environment["FLORESTA_REPLACE_BTCPAY_BACKEND"] = Environment.GetEnvironmentVariable("FLORESTA_REPLACE_BTCPAY_BACKEND") ?? "true";
        psi.Environment["FLORESTA_NETWORK"] = Environment.GetEnvironmentVariable("FLORESTA_NETWORK") ?? "regtest";
        psi.Environment["FLORESTA_ELECTRUM_HOST"] = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_HOST") ?? "127.0.0.1";
        psi.Environment["FLORESTA_ELECTRUM_PORT"] = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_PORT") ?? "20001";
        psi.Environment["FLORESTA_ELECTRUM_TLS"] = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_TLS") ?? "false";
        psi.Environment["FLORESTA_RPC_URL"] = Environment.GetEnvironmentVariable("FLORESTA_RPC_URL") ?? "http://127.0.0.1:18442";
        psi.Environment["FLORESTA_GAP_LIMIT"] = Environment.GetEnvironmentVariable("FLORESTA_GAP_LIMIT") ?? "100";
        psi.Environment["FLORESTA_FALLBACK_FEE_SAT_PER_VB"] = Environment.GetEnvironmentVariable("FLORESTA_FALLBACK_FEE_SAT_PER_VB") ?? "1";
        psi.Environment["FLORESTA_FILTERS_START_HEIGHT"] = Environment.GetEnvironmentVariable("FLORESTA_FILTERS_START_HEIGHT") ?? "0";

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start BTCPay Server process.");
        _process.OutputDataReceived += (_, e) => AppendOutput(e.Data);
        _process.ErrorDataReceived += (_, e) => AppendOutput(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForServerAsync(TimeSpan.FromSeconds(GetTimeoutSeconds()));
    }

    public async Task DisposeAsync()
    {
        await StopProcessAsync();
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // The test result should not depend on temp directory cleanup.
        }
    }

    private async Task StopProcessAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch
            {
                // Best-effort cleanup for test processes.
            }
        }

        _process?.Dispose();
        _process = null;
    }

    private async Task WaitForServerAsync(TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException($"BTCPay Server exited with code {_process.ExitCode}.\n{Output}");

            try
            {
                using var response = await http.GetAsync(ServerUri);
                if ((int)response.StatusCode < 500)
                    return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"BTCPay Server did not become ready at {ServerUri}. Last error: {lastError?.Message}\n{Output}");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BTCPayServer.Plugins.Floresta.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "..", "..", "..", ".."));
    }

    private static int GetTimeoutSeconds()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("E2E_READY_TIMEOUT_SECONDS"), out var seconds)
            ? seconds
            : 180;
    }

    private void AppendOutput(string? line)
    {
        if (line is null)
            return;
        lock (_output)
            _output.AppendLine(line);
    }
}
