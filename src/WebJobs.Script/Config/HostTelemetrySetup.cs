using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel;
using Microsoft.Azure.WebJobs.Logging.ApplicationInsights;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;

namespace Microsoft.Azure.WebJobs.Script.Config
{
    internal class HostTelemetrySetup(HostBuilderContext ctx, IServiceCollection services) : IPostConfigureOptions<ScriptJobHostOptions>
    {
        private readonly HostBuilderContext _context = ctx;
        private readonly IServiceCollection _services = services;

        public void PostConfigure(string name, ScriptJobHostOptions options)
        {
            if (options.TelemetryMode is TelemetryMode.OpenTelemetry)
            {
                var instanceId = options?.InstanceId;
                if (!string.IsNullOrWhiteSpace(instanceId))
                {
                    _services.AddOpenTelemetry().ConfigureResource(r => r.AddAttributes([new(ScriptConstants.LogPropertyHostInstanceIdKey, instanceId)]));
                }

                _services.AddLogging(b => b.ConfigureOpenTelemetry());
            }
            else if (options.TelemetryMode is TelemetryMode.ApplicationInsights)
            {
                // Telemetry will only be put out by AppInsights if the env vars for key or connstring are also set, so this is safe to call always.
                _services.AddLogging(b => ConfigureApplicationInsights(_context, b));
            }
        }

        internal static void ConfigureApplicationInsights(HostBuilderContext context, ILoggingBuilder builder)
        {
            string appInsightsInstrumentationKey = context.Configuration[EnvironmentSettingNames.AppInsightsInstrumentationKey];
            string appInsightsConnectionString = context.Configuration[EnvironmentSettingNames.AppInsightsConnectionString];

            // Initializing AppInsights services during placeholder mode as well to avoid the cost of JITting these objects during specialization
            if (!string.IsNullOrEmpty(appInsightsInstrumentationKey) || !string.IsNullOrEmpty(appInsightsConnectionString) || SystemEnvironment.Instance.IsPlaceholderModeEnabled())
            {
                builder.AddApplicationInsightsWebJobs(o =>
                {
                    o.InstrumentationKey = appInsightsInstrumentationKey;
                    o.ConnectionString = appInsightsConnectionString;
                }, t =>
                {
                    if (t.TelemetryChannel is ServerTelemetryChannel channel)
                    {
                        channel.TransmissionStatusEvent += TransmissionStatusHandler.Handler;
                    }

                    t.TelemetryProcessorChainBuilder.Use(next => new WorkerTraceFilterTelemetryProcessor(next));
                    t.TelemetryProcessorChainBuilder.Use(next => new ScriptTelemetryProcessor(next));
                });

                builder.Services.ConfigureOptions<ApplicationInsightsLoggerOptionsSetup>();
                builder.Services.AddSingleton<ISdkVersionProvider, FunctionsSdkVersionProvider>();
                builder.Services.AddSingleton<ITelemetryInitializer, ScriptTelemetryInitializer>();

                if (SystemEnvironment.Instance.IsPlaceholderModeEnabled())
                {
                    // Disable auto-http and dependency tracking when in placeholder mode.
                    builder.Services.Configure<ApplicationInsightsLoggerOptions>(o =>
                    {
                        o.HttpAutoCollectionOptions.EnableHttpTriggerExtendedInfoCollection = false;
                        o.EnableDependencyTracking = false;
                    });
                }
            }
        }

    }
}
