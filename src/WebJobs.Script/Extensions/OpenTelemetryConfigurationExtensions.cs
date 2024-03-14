using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using System;
using System.Diagnostics;

namespace Microsoft.Azure.WebJobs.Script.Extensions
{
    internal static class OpenTelemetryConfigurationExtensions
    {
        private static class OpenTelemetryConfigurationSectionNames
        {
            public const string OpenTelemetry = "openTelemetry";
            public const string Metrics = "metrics";
            public const string EnabledMetrics = "enabledMetrics";
            public const string FunctionsRuntimeMetrics = "Functions.Runtime";
            public const string Traces = "traces";
            public const string EnabledTraces = "enabledTraces";
            public const string FunctionsRuntimeInstrumentationTraces = "FunctionsRuntimeInstrumentation";
        }

        public static void ConfigureOpenTelemetry(this ILoggingBuilder loggingBuilder, out bool appInsightsConfigured)
        {
            appInsightsConfigured = false;

            // OpenTelemetry configuration for the host is specified in host.json
            // It follows the schema used by OpenTelemetry.NET's support for IOptions
            // See https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/trace/customizing-the-sdk/README.md#configuration-files-and-environment-variables

            if (bool.TryParse(Environment.GetEnvironmentVariable(EnvironmentSettingNames.OtelSdkDisabled) ?? bool.TrueString, out var b) && b)
            {
                return;
            }

            loggingBuilder.AddOpenTelemetry(c => c.AddOtlpExporter())
                // These are messages piped back to the host from the worker - we don't handle these anymore if the worker has appinsights enabled.
                // Instead, we expect the user's own code to be logging these where they want them to go.
                .AddFilter("Host.Function.Console", _ => !ScriptHost.WorkerApplicationInsightsLoggingEnabled)
                .AddFilter("Function.*", _ => !ScriptHost.WorkerApplicationInsightsLoggingEnabled);    // Function.* also removes 'Executing' & 'Executed' logs which we don't need in OpenTelemetry-based executions as Activities encompass these.

            loggingBuilder.Services.AddOpenTelemetry()
                .ConfigureResource(r =>
                {
                    var version = typeof(ScriptHost).Assembly.GetName().Version.ToString();
                    r.AddService("azureFunctions", serviceVersion: version);
                    // Set the AI SDK to a key so we know all the telemetry came from the Functions Host
                    // NOTE: This ties to \azure-sdk-for-net\sdk\monitor\Azure.Monitor.OpenTelemetry.Exporter\src\Internals\ResourceExtensions.cs :: AiSdkPrefixKey used in CreateAzureMonitorResource()
                    r.AddAttributes([
                        new("ai.sdk.prefix", $@"azurefunctionscoretools: {version} "),
                        new("azurefunctionscoretools_version", version),
                        new(ScriptConstants.LogPropertyProcessIdKey, Process.GetCurrentProcess().Id)
                    ]);
                })
                .WithTracing(c => c
                    .AddProcessor(OtelActivitySanitizingProcessor.Instance)
                    .AddProcessor(OtelWorkerTraceFilterProcessor.Instance)
                    .AddOtlpExporter())
                .WithMetrics(c => c.AddOtlpExporter())
                .UseAzureMonitor();
        }

        public static void AddHostInstanceIdToOpenTelemetry(this IServiceCollection services)
        {
            if (bool.TryParse(Environment.GetEnvironmentVariable(EnvironmentSettingNames.OtelSdkDisabled) ?? bool.TrueString, out var b) && !b)
            {
                services.AddOpenTelemetry().ConfigureResource(r =>
                {
                    ServiceProvider sp = services.BuildServiceProvider();
                    var o = sp.GetService<IOptions<ScriptJobHostOptions>>().Value;

                    //sp.GetService<IOptions<ScriptJobHostOptions>>();
                    var instanceId = o.InstanceId;
                    if (!string.IsNullOrWhiteSpace(instanceId))
                    {
                        r.AddAttributes([
                            new(ScriptConstants.LogPropertyHostInstanceIdKey, instanceId)
                        ]);

                        Environment.SetEnvironmentVariable(ScriptConstants.LogPropertyHostInstanceIdKey, instanceId);
                    }
                });
            }
        }

        public static void AddOpenTelemetryConfigurations(this IConfigurationBuilder configBuilder, HostBuilderContext context)
        {
            // .NET would pick up otel config automatically if we stored it at <root> { Metrics { ...
            // but we've chosen to put it at <root> { openTelemetry { Metrics { ...
            // so, we need to change its structure accordingly, then manually add it to the config builder so .NET picks it up as though it were in the format expected
            var customOtelSection = context.Configuration.GetSection(ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, OpenTelemetryConfigurationSectionNames.Metrics, OpenTelemetryConfigurationSectionNames.OpenTelemetry));
            if (customOtelSection.Exists())
            {
                customOtelSection = TranslateEnabledMetricValues(customOtelSection);

                // Create a new configuration that removes the 'openTelemetry' layer
                var newConfigBuilder = new ConfigurationBuilder();
                newConfigBuilder.AddInMemoryCollection([new(OpenTelemetryConfigurationSectionNames.Metrics, customOtelSection.Value)]);

                configBuilder.AddConfiguration(newConfigBuilder.Build());
            }

            // Do the same with 'traces' section
            customOtelSection = context.Configuration.GetSection(ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, OpenTelemetryConfigurationSectionNames.Traces, OpenTelemetryConfigurationSectionNames.OpenTelemetry));
            if (customOtelSection.Exists())
            {
                customOtelSection = TranslateEnabledTraceValues(customOtelSection);

                // Create a new configuration that removes the 'openTelemetry' layer
                var newConfigBuilder = new ConfigurationBuilder();
                newConfigBuilder.AddInMemoryCollection([new(OpenTelemetryConfigurationSectionNames.Traces, customOtelSection.Value)]);

                configBuilder.AddConfiguration(newConfigBuilder.Build());
            }
        }

        /// <summary>
        /// Translates the enabled metric values in the specified custom OpenTelemetry section.
        /// </summary>
        /// <param name="customOtelSection">The custom OpenTelemetry section.</param>
        /// <returns>The translated custom OpenTelemetry section.</returns>
        private static IConfigurationSection TranslateEnabledMetricValues(IConfigurationSection customOtelSection)
        {
            // if there's an entry under 'enabledMetrics' with a key of 'Functions.Runtime', rename it to 'Microsoft.AspNet.Core'
            var enabledMetrics = customOtelSection.GetSection(OpenTelemetryConfigurationSectionNames.EnabledMetrics);
            if (enabledMetrics.Exists())
            {
                var functionsRuntime = enabledMetrics.GetSection(OpenTelemetryConfigurationSectionNames.FunctionsRuntimeMetrics);
                if (functionsRuntime.Exists())
                {
                    enabledMetrics["Microsoft.AspNet.Core"] = functionsRuntime.Value;
                    enabledMetrics.GetSection(OpenTelemetryConfigurationSectionNames.FunctionsRuntimeMetrics).Value = null;
                }
            }

            return customOtelSection;
        }

        /// <summary>
        /// Translates the enabled trace values in the customOtelSection configuration.
        /// If there is an entry under 'enabledTraces' with a key of 'FunctionsRuntimeInstrumentation',
        /// it renames it to 'AspNetCoreInstrumentation'.
        /// </summary>
        private static IConfigurationSection TranslateEnabledTraceValues(IConfigurationSection customOtelSection)
        {
            // if there's an entry under 'enabledTraces' with a key of 'FunctionsRuntimeInstrumentation', rename it to 'AspNetCoreInstrumentation'
            var enabledTraces = customOtelSection.GetSection(OpenTelemetryConfigurationSectionNames.EnabledTraces);
            if (enabledTraces.Exists())
            {
                var functionsRuntimeInstrumentation = enabledTraces.GetSection(OpenTelemetryConfigurationSectionNames.FunctionsRuntimeInstrumentationTraces);
                if (functionsRuntimeInstrumentation.Exists())
                {
                    enabledTraces["AspNetCoreInstrumentation"] = functionsRuntimeInstrumentation.Value;
                    enabledTraces.GetSection(OpenTelemetryConfigurationSectionNames.FunctionsRuntimeInstrumentationTraces).Value = null;
                }
            }

            return customOtelSection;
        }
    }
}
