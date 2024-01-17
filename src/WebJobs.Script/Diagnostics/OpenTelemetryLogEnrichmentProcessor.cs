using Microsoft.Azure.WebJobs.Logging.ApplicationInsights;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Microsoft.Azure.WebJobs.Script.Diagnostics
{
    internal class OpenTelemetryLogEnrichmentProcessor(IOptions<ScriptJobHostOptions> hostOptions, IConfigureOptions<ApplicationInsightsLoggerOptions> appInsightsOptions) : OpenTelemetryBaseEnrichmentProcessor<LogRecord>(hostOptions)
    {
        private readonly IConfigureOptions<ApplicationInsightsLoggerOptions> _appInsightsOptions = appInsightsOptions;

        protected override void OnEndInternal(LogRecord data)
        {
            // If we've registered application insights SDK, skip sending this data on else it will be logged in duplicate
            if (data.CategoryName is not WorkerConstants.ConsoleLogCategoryName)
            {
                data.TraceFlags |= ActivityTraceFlags.Recorded;
            }
        }

        protected override void AddHostInstanceId(LogRecord data, string hostInstanceId)
        {
            var newAttributes = new List<KeyValuePair<string, object>>(data.Attributes ?? Array.Empty<KeyValuePair<string, object>>())
            {
                new(ScriptConstants.LogPropertyHostInstanceIdKey, hostInstanceId)
            };
            data.Attributes = newAttributes;
        }

        protected override void AddProcessId(LogRecord data)
        {
            bool hasEventName = data.Attributes.Any(data => data.Key == ScriptConstants.LogPropertyEventNameKey);
            var newAttributes = new List<KeyValuePair<string, object>>(data.Attributes ?? Array.Empty<KeyValuePair<string, object>>())
            {
                new(ScriptConstants.LogPropertyProcessIdKey, System.Diagnostics.Process.GetCurrentProcess().Id),
                new("Telemetry type", "logg")
            };
            data.Attributes = newAttributes;
        }
    }
}
