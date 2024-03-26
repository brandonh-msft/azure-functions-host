using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenTelemetry.Resources;

namespace Microsoft.Azure.WebJobs.Script.Config
{
    internal class HostInstancIdOTelWireup(IServiceCollection services) : IPostConfigureOptions<ScriptJobHostOptions>
    {
        public void PostConfigure(string name, ScriptJobHostOptions options)
        {
            if (options.TelemetryMode is TelemetryMode.OpenTelemetry)
            {
                var instanceId = options?.InstanceId;
                if (!string.IsNullOrWhiteSpace(instanceId))
                {
                    services.AddOpenTelemetry().ConfigureResource(r => r.AddAttributes([new(ScriptConstants.LogPropertyHostInstanceIdKey, instanceId)]));
                }
            }
        }
    }
}
