namespace WebPass.Web.Application.Ping;

public sealed record PingProbeResult(
    string TargetIp,
    string Outcome,
    long? LatencyMilliseconds,
    string? ErrorCode);
