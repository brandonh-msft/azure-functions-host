using Microsoft.Azure.WebJobs.Logging;
using OpenTelemetry;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Microsoft.Azure.WebJobs.Script.Diagnostics
{
    internal class OtelActivitySanitizingProcessor : BaseProcessor<Activity>
    {
        public static OtelActivitySanitizingProcessor Instance { get; } = new OtelActivitySanitizingProcessor();

        private OtelActivitySanitizingProcessor() { }

        public override void OnEnd(Activity data)
        {
            Sanitize(data);

            base.OnEnd(data);
        }

        private static readonly ImmutableArray<string> TagsToSanitize = ImmutableArray.Create("url.query", "url.full");

        private static void Sanitize(Activity data)
        {
            foreach (var t in TagsToSanitize)
            {
                if (data.GetTagItem(t) is string s and not null)
                {
                    var sanitizedValue = Sanitizer.Sanitize(s);
                    data.SetTag(t, sanitizedValue);
                }
            }
        }
    }

    internal class OtelWorkerTraceFilterProcessor : BaseProcessor<Activity>
    {
        public static OtelWorkerTraceFilterProcessor Instance { get; } = new OtelWorkerTraceFilterProcessor();

        private OtelWorkerTraceFilterProcessor() { }

        public override void OnEnd(Activity data)
        {
            var dataTags = data.Tags.ToImmutableDictionary();

            DropDependencyTracesToAppInsightsEndpoints(data, dataTags);

            DropDependencyTracesToHostStorageEndpoints(data, dataTags);

            DropDependencyTracesToHostLoopbackEndpoints(data, dataTags);

            base.OnEnd(data);
        }

        private void DropDependencyTracesToHostLoopbackEndpoints(Activity data, IImmutableDictionary<string, string> dataTags)
        {
            if (data.ActivityTraceFlags is ActivityTraceFlags.Recorded)
            {
                var url = GetUrlTagValue(dataTags);
                if (url?.Contains("/AzureFunctionsRpcMessages.FunctionRpc/", System.StringComparison.OrdinalIgnoreCase) is true
                    || url?.EndsWith("/getScriptTag", System.StringComparison.OrdinalIgnoreCase) is true)
                {
                    data.ActivityTraceFlags = ActivityTraceFlags.None;
                }
            }
        }

        private string GetUrlTagValue(IImmutableDictionary<string, string> dataTags)
        {
            string url;
            _ = dataTags.TryGetValue("url.full", out url) || dataTags.TryGetValue("http.url", out url);
            return url;
        }

        private void DropDependencyTracesToAppInsightsEndpoints(Activity data, IImmutableDictionary<string, string> dataTags)
        {
            if (data.ActivityTraceFlags is ActivityTraceFlags.Recorded
                && data.Source.Name is "Azure.Core.Http" or "System.Net.Http")
            {
                string url = GetUrlTagValue(dataTags);
                if (url?.Contains("applicationinsights.azure.com", System.StringComparison.OrdinalIgnoreCase) is true
                    || url?.Contains("rt.services.visualstudio.com/QuickPulseService.svc", System.StringComparison.OrdinalIgnoreCase) is true)
                {
                    // don't record all the HTTP calls to Live Stream aka QuickPulse
                    data.ActivityTraceFlags = ActivityTraceFlags.None;
                }
            }
        }

        private void DropDependencyTracesToHostStorageEndpoints(Activity data, IImmutableDictionary<string, string> dataTags)
        {
            if (data.ActivityTraceFlags is ActivityTraceFlags.Recorded)
            {
                if (data.Source.Name is "Azure.Core.Http" or "System.Net.Http"
                    && dataTags.TryGetValue("az.namespace", out string azNamespace)
                    && azNamespace is "Microsoft.Storage")
                {
                    string url = GetUrlTagValue(dataTags);
                    if (url?.Contains("/azure-webjobs-", System.StringComparison.OrdinalIgnoreCase) is true)
                {
                    // don't record all the HTTP calls to backing storage used by the host
                    data.ActivityTraceFlags = ActivityTraceFlags.None;
                }
            }
        }
        }
    }
}
