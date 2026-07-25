using System.Net;
using System.Net.NetworkInformation;
using WebPass.Web.Application.Ping;

namespace WebPass.Web.Infrastructure.Networking;

public sealed class SystemPingTransport : IPingTransport
{
    public async Task<PingTransportResult> SendAsync(string targetIp, int timeoutMilliseconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IPAddress.TryParse(targetIp, out var address))
            return new PingTransportResult("InternalError", null, "InvalidAddress");

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMilliseconds);
            return reply.Status switch
            {
                IPStatus.Success => new PingTransportResult("Success", reply.RoundtripTime, null),
                IPStatus.TimedOut => new PingTransportResult("Timeout", null, "TimedOut"),
                _ => new PingTransportResult("Unreachable", null, "Unreachable"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new PingTransportResult("InternalError", null, "TransportError");
        }
    }
}
