using System.Diagnostics;

namespace Custodian.Shared.Observability;

public static class Telemetry
{
    public static ActivitySource CreateSource(string serviceName) =>
        new($"Custodian.{serviceName}");
}