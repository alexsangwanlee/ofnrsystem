using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Ofnr.Server.Jobs;

/// <summary>Whether the desktop app behind a recipe lock can take a job right now.</summary>
public static class AppReadiness
{
    /// <returns>Null when ready, else what the CEO has to do first.</returns>
    public static async Task<string?> CheckAsync(string lockName, CancellationToken ct = default)
    {
        switch (lockName)
        {
            case "sketchup":
                using (var tcp = new TcpClient())
                {
                    try { await tcp.ConnectAsync(IPAddress.Loopback, 9876, ct); }
                    catch (SocketException) { return "SketchUp을 열고 MCP Server를 Start 해 주세요."; }
                }
                return null;
            case "illustrator":
                var running = Process.GetProcessesByName("Illustrator");
                foreach (var p in running) p.Dispose();
                return running.Length > 0 ? null : "Illustrator를 실행해 주세요.";
            default:
                return null; // office: Excel and PowerPoint start on demand through COM
        }
    }
}
