using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter.Geneva;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Microsoft.Azure.WebJobs.Script.Extensions
{
    internal static class OpenTelemetryConfigurationExtensions
    {
        private static class OpenTelemetryConfigurationSectionNames
        {
            public const string OpenTelemetry = "openTelemetry";
            public const string Logs = "logs";
            public const string LoggingFilters = "filters";
            public const string Exporters = "exporters";
            public const string ConstantExporter = "console";
            public const string GenevaExporter = "geneva";
            public const string AzureMonitorExporter = "azureMonitor";
            public const string Metrics = "metrics";
            public const string EnabledMetrics = "enabledMetrics";
            public const string FunctionsRuntimeMetrics = "Functions.Runtime";
            public const string Traces = "traces";
            public const string EnabledTraces = "enabledTraces";
            public const string FunctionsRuntimeInstrumentationTraces = "FunctionsRuntimeInstrumentation";
            public const string Resources = "resources";
            public const string ResourceAttributes = "attributes";
        }

        public static void ConfigureOpenTelemetry(this ILoggingBuilder loggingBuilder, HostBuilderContext context, out bool appInsightsConfigured)
        {
            appInsightsConfigured = false;

            // OpenTelemetry configuration for the host is specified in host.json
            // It follows the schema used by OpenTelemetry.NET's support for IOptions
            // See https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/trace/customizing-the-sdk/README.md#configuration-files-and-environment-variables

            if (bool.TryParse(Environment.GetEnvironmentVariable(EnvironmentSettingNames.OtelSdkDisabled) ?? bool.TrueString, out var b) && b)
            {
                return;
            }

            var services = loggingBuilder.Services;
            OpenTelemetryBuilder otBuilder = services.AddOpenTelemetry()
                .WithTracing(c => c.AddProcessor(OtelActivitySanitizingProcessor.Instance));

            var specificOtelConfig = context.Configuration.GetSection(ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, ConfigurationSectionNames.Logging, OpenTelemetryConfigurationSectionNames.OpenTelemetry));
            if (specificOtelConfig.Exists())
            {
                loggingBuilder.AddOpenTelemetry(specificOtelConfig.OtelBind);
            }

            loggingBuilder
                // These are messages piped back to the host from the worker - we don't handle these anymore if the worker has appinsights enabled.
                // Instead, we expect the user's own code to be logging these where they want them to go.
                .AddFilter("Host.Function.Console", (level) => !ScriptHost.WorkerApplicationInsightsLoggingEnabled)
                .AddFilter("Function.*", (level) => !ScriptHost.WorkerApplicationInsightsLoggingEnabled);    // Function.* also removes 'Executing' & 'Executed' logs which we don't need in OpenTelemetry-based executions as Activities encompass these.

            // Configure opentelemetry exporters from host.config / opentelemetry / exporters across all 3 avenues
            var exporterConfig = context.Configuration.GetSection(ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, OpenTelemetryConfigurationSectionNames.OpenTelemetry, OpenTelemetryConfigurationSectionNames.Exporters));
            foreach (var section in exporterConfig.GetChildren())
            {
                RegisterExporter(loggingBuilder, otBuilder, section,
                    ExporterType.Logging | ExporterType.Traces | ExporterType.Metrics,
                    ref appInsightsConfigured);
            }

            // If the user configured exporters outside global (/opentelemetry/exporters) config, apply those now
            // If they have the same name, they'll be overridden by these registrations, as intended

            // Configure Otel Logging based on host.config / logging / openTelemetry
            specificOtelConfig = context.Configuration.GetSection(ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, ConfigurationSectionNames.Logging, OpenTelemetryConfigurationSectionNames.OpenTelemetry, OpenTelemetryConfigurationSectionNames.Exporters));
            foreach (var section in specificOtelConfig.GetChildren())
            {
                RegisterExporter(loggingBuilder, otBuilder, section, ExporterType.Logging, ref appInsightsConfigured);
            }

            // Configure Otel Logging based on host.config / metrics / openTelemetry
            specificOtelConfig = context.Configuration.GetSection(ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, OpenTelemetryConfigurationSectionNames.Metrics, OpenTelemetryConfigurationSectionNames.OpenTelemetry, OpenTelemetryConfigurationSectionNames.Exporters));
            foreach (var section in specificOtelConfig.GetChildren())
            {
                RegisterExporter(otBuilder, section, ExporterType.Metrics);
            }

            // Configure Otel Logging based on host.config / traces / openTelemetry
            specificOtelConfig = context.Configuration.GetSection(ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, OpenTelemetryConfigurationSectionNames.Traces, OpenTelemetryConfigurationSectionNames.OpenTelemetry, OpenTelemetryConfigurationSectionNames.Exporters));
            foreach (var section in specificOtelConfig.GetChildren())
            {
                RegisterExporter(otBuilder, section, ExporterType.Traces);
            }

            otBuilder
                .ConfigureResource(rb => ConfigureOpenTelemetryResourceBuilder(context, rb));
        }

        [Flags]
        private enum ExporterType
        {
            Logging = 0b0001,
            Metrics = 0b0010,
            Traces = 0b0100,
        }

        private static void OtelBind<T>(this IConfigurationSection section, T options) => section.Bind(options, bo => bo.BindNonPublicProperties = true);

        private static readonly ImmutableArray<string> WellKnownOpenTelemetryExporters = ImmutableArray.Create(OpenTelemetryConfigurationSectionNames.ConstantExporter, OpenTelemetryConfigurationSectionNames.GenevaExporter, OpenTelemetryConfigurationSectionNames.AzureMonitorExporter);

        private static void RegisterExporter(OpenTelemetryBuilder otBuilder, IConfigurationSection section, ExporterType type)
        {
            bool throwaway = false;
            RegisterExporter(null, otBuilder, section, type, ref throwaway);
        }

        private static void RegisterExporter(ILoggingBuilder loggingBuilder, OpenTelemetryBuilder otBuilder, IConfigurationSection section, ExporterType type, ref bool appInsightsConfigured)
        {
            if (!WellKnownOpenTelemetryExporters.Contains(section.Key, StringComparer.OrdinalIgnoreCase))
            {
                FlagSwitch(type, new (ExporterType, Action)[] {
                    (ExporterType.Logging, () => loggingBuilder.AddOpenTelemetry(o => o.AddOtlpExporter(section.Key, section.Bind))),
                    (ExporterType.Metrics, () => otBuilder.WithMetrics(o => o.AddOtlpExporter(section.Key, section.Bind))),
                    (ExporterType.Traces, () => otBuilder.WithTracing(o => o.AddOtlpExporter(section.Key, section.Bind)))
                });
            }
            else
            {
                // If AzureMonitor configuration is set, we have to wire that up a special way
                if (section.Key.Equals(OpenTelemetryConfigurationSectionNames.AzureMonitorExporter, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Assert(type.HasFlag(ExporterType.Logging), "Azure Monitor sections should be labeled as logging exporter type");

                    appInsightsConfigured = true;

                    otBuilder.UseAzureMonitor(section.OtelBind);

                    // Facilitate worker's use of ApplicationInsights
                    Environment.SetEnvironmentVariable(EnvironmentSettingNames.AppInsightsConnectionString, section["ConnectionString"]);

                    // Ignore azureMonitor at the traces/metrics level because 'Distro' only supports one definition; we've chosen to use the 'logging'
                }
                else if (section.Key.Equals(OpenTelemetryConfigurationSectionNames.GenevaExporter, StringComparison.OrdinalIgnoreCase))
                {
                    FlagSwitch(type, new (ExporterType, Action)[] {
                        (ExporterType.Logging, () => loggingBuilder.AddOpenTelemetry(b => b.AddGenevaLogExporter(section.OtelBind))),
                        (ExporterType.Metrics, () => otBuilder.WithMetrics(b => b.AddGenevaMetricExporter(section.OtelBind))),
                        (ExporterType.Traces, () => otBuilder.WithTracing(b => b.AddGenevaTraceExporter(section.OtelBind)))
                    });
                }
                else if (section.Key.Equals(OpenTelemetryConfigurationSectionNames.ConstantExporter, StringComparison.OrdinalIgnoreCase))
                {
                    FlagSwitch(type, new (ExporterType, Action)[] {
                        (ExporterType.Logging, () => loggingBuilder.AddOpenTelemetry(b => b.AddConsoleExporter(section.OtelBind))),
                        (ExporterType.Metrics, () => otBuilder.WithMetrics(b => b.AddConsoleExporter(section.OtelBind))),
                        (ExporterType.Traces, () => otBuilder.WithTracing(b => b.AddConsoleExporter(section.OtelBind)))
                    });
                }
                else
                {
                    Debug.Fail($@"Unhandled 'Well Known' otel exporter: {section.Key}");
                }
            }
        }

        private static void FlagSwitch<T>(T value, IEnumerable<(T Value, Action Action)> actions) where T : Enum
        {
            // Check if the enum has the Flags attribute
            _ = typeof(T).GetCustomAttribute<FlagsAttribute>() ?? throw new ArgumentException($@"The provided enum '{typeof(T).FullName}' does not have the [Flags] attribute.");

            foreach (var a in actions)
            {
                if (value.HasFlag(a.Value))
                {
                    a.Action();
                }
            }
        }

        private static void ConfigureOpenTelemetryResourceBuilder(HostBuilderContext context, ResourceBuilder r)
        {
            // Configure resources from host.config / opentelemetry / resources
            var configuredResources = context.Configuration.GetSection(ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, OpenTelemetryConfigurationSectionNames.OpenTelemetry, OpenTelemetryConfigurationSectionNames.Resources)).GetChildren();
            if (configuredResources?.Any() is true)
            {
                foreach (var rConfig in configuredResources)
                {
                    r.AddService(rConfig["serviceName"].ToString(),
                        rConfig["serviceNamespace"],
                        rConfig["serviceVersion"],
                        !bool.TryParse(rConfig["autoGenerateServiceInstanceId"] ?? bool.TrueString, out var b) || b,
                        rConfig["serviceInstanceId"]);

                    var attributes = rConfig.GetSection(OpenTelemetryConfigurationSectionNames.ResourceAttributes).GetChildren();
                    r.AddAttributes(attributes.Select(a => new KeyValuePair<string, object>(a.Key, a.Value)));
                }
            }

            // Set the AI SDK to a key so we know all the telemetry came from the Functions Host
            // NOTE: This ties to \azure-sdk-for-net\sdk\monitor\Azure.Monitor.OpenTelemetry.Exporter\src\Internals\ResourceExtensions.cs :: AiSdkPrefixKey used in CreateAzureMonitorResource()
            var version = typeof(ScriptHost).Assembly.GetName().Version.ToString();
            r.AddAttributes([
                new KeyValuePair<string, object>("ai.sdk.prefix", $@"azurefunctionscoretools: {version} "),
                new KeyValuePair<string, object>("azurefunctionscoretools_version", version)
            ]);
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
                newConfigBuilder.AddInMemoryCollection(new Dictionary<string, string>
                {
                    { ConfigurationPath.Combine(OpenTelemetryConfigurationSectionNames.Metrics), customOtelSection.Value }
                });

                configBuilder.AddConfiguration(newConfigBuilder.Build());
            }

            // Do the same with 'traces' section
            customOtelSection = context.Configuration.GetSection(ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, OpenTelemetryConfigurationSectionNames.Traces, OpenTelemetryConfigurationSectionNames.OpenTelemetry));
            if (customOtelSection.Exists())
            {
                customOtelSection = TranslateEnabledTraceValues(customOtelSection);

                // Create a new configuration that removes the 'openTelemetry' layer
                var newConfigBuilder = new ConfigurationBuilder();
                newConfigBuilder.AddInMemoryCollection(new Dictionary<string, string>
                {
                    { ConfigurationPath.Combine(OpenTelemetryConfigurationSectionNames.Traces), customOtelSection.Value }
                });

                configBuilder.AddConfiguration(newConfigBuilder.Build());
            }
        }

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
