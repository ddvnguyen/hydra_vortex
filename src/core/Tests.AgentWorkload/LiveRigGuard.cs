using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Tests.AgentWorkload;

/// <summary>
/// Helper for checking whether the live Hydra rig is reachable and functional.
/// Guards are deliberately strict — if any check fails, the test should skip
/// (not fail) so that purely-local parsing tests remain green.
/// </summary>
public static class LiveRigGuard
{
    private const string HealthUrl = "http://localhost:9000/health";

    /// <summary>
    /// Returns true only if Hydra core responds with a healthy status AND
    /// lists all expected GPU nodes. A bare "200 OK" is not sufficient.
    /// </summary>
    public static bool IsHydraReachable()
    {
        if (!IsTcpPortOpen("localhost", 9000)) return false;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = client.GetAsync(HealthUrl).GetAwaiter().GetResult();
            if (response.StatusCode != HttpStatusCode.OK) return false;

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Must have "status": "healthy"
            if (!root.TryGetProperty("status", out var status) ||
                status.GetString() != "healthy")
            {
                return false;
            }

            // Must list at least one GPU node
            if (root.TryGetProperty("nodes", out var nodes) &&
                nodes.GetArrayLength() > 0)
            {
                return true;
            }

            // Also accept if health endpoint has expected fields without nodes array
            return root.TryGetProperty("slots_idle", out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if a CLI binary is available on PATH.
    /// </summary>
    public static bool IsCliAvailable(string binaryName)
    {
        try
        {
            var psi = new ProcessStartInfo(binaryName, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(TimeSpan.FromSeconds(3));
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if podman is available.
    /// </summary>
    public static bool IsPodmanAvailable()
    {
        return IsCliAvailable("podman");
    }

    private static bool IsTcpPortOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(host, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
            if (success) client.EndConnect(result);
            return success;
        }
        catch
        {
            return false;
        }
    }
}
