namespace Microsoft.Azure.WebJobs.Script.Diagnostics
{
    internal enum TelemetryMode
    {
        ApplicationInsights = 0b0000,
        OpenTelemetry = 0b0001
    }
}
