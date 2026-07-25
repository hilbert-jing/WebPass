namespace WebPass.Web.Application.Ping;

public sealed record PingTransportResult(string Outcome, long? LatencyMilliseconds, string? ErrorCode);

public interface IPingTransport
{
    Task<PingTransportResult> SendAsync(string targetIp, int timeoutMilliseconds, CancellationToken ct);
}
