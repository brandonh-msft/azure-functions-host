using Microsoft.Azure.WebJobs.Logging;
using OpenTelemetry;
using System.Collections.Immutable;
using System.Diagnostics;

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
}
