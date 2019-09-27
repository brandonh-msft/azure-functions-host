﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.IO.Abstractions;
using System.Net.Http;
using Autofac;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.WebHost.Management;
using Microsoft.Azure.WebJobs.Script.WebHost.WebHooks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.WebHost
{
    public static class AutofacBootstrap
    {
        internal static void Initialize(ScriptSettingsManager settingsManager, ContainerBuilder builder, WebHostSettings settings)
        {
            builder.RegisterInstance(settingsManager);
            builder.RegisterInstance(settings);
            builder.Register<IFileSystem>(_ => FileUtility.Instance).SingleInstance();

            builder.RegisterType<WebHostResolver>().SingleInstance();
            builder.RegisterType<DefaultSecretManagerFactory>().As<ISecretManagerFactory>().SingleInstance();
            builder.RegisterType<ScriptEventManager>().As<IScriptEventManager>().SingleInstance();

            // these services are externally owned by the WebHostResolver, and will be disposed
            // when the resolver is disposed
            builder.Register<TraceWriter>(ct => ct.Resolve<WebHostResolver>().GetTraceWriter(settings)).ExternallyOwned();
            builder.Register<ISecretManager>(ct => ct.Resolve<WebHostResolver>().GetSecretManager(settings)).ExternallyOwned();
            builder.Register<ISwaggerDocumentManager>(ct => ct.Resolve<WebHostResolver>().GetSwaggerDocumentManager(settings)).ExternallyOwned();
            builder.Register<WebScriptHostManager>(ct => ct.Resolve<WebHostResolver>().GetWebScriptHostManager(settings)).ExternallyOwned();
            builder.Register<WebHookReceiverManager>(ct => ct.Resolve<WebHostResolver>().GetWebHookReceiverManager(settings)).ExternallyOwned();
            builder.Register<ILoggerFactory>(ct => ct.Resolve<WebHostResolver>().GetLoggerFactory(settings)).ExternallyOwned();
            builder.Register<IFunctionsSyncManager>(ct => ct.Resolve<WebHostResolver>().GetFunctionsSyncManager(settings)).ExternallyOwned();
        }
    }
}