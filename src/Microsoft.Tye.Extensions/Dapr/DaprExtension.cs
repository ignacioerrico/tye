﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Microsoft.Tye.Extensions.Dapr
{
    internal sealed class DaprExtension : Extension
    {
        public override async Task ProcessAsync(ExtensionContext context, ExtensionConfiguration config)
        {
            // If we're getting called then the user configured dapr in their tye.yaml.
            // We don't have any of our own config.

            if (context.Operation == ExtensionContext.OperationKind.LocalRun)
            {
                await VerifyDaprInitialized(context);

                var extensionConfiguration = DaprExtensionConfigurationReader.ReadConfiguration(config.Data);

                // For local run, enumerate all projects, and add services for each dapr proxy.
                var projects = context.Application.Services.OfType<ProjectServiceBuilder>().Cast<LaunchedServiceBuilder>();
                var executables = context.Application.Services.OfType<ExecutableServiceBuilder>().Cast<LaunchedServiceBuilder>();
                var services = projects.Concat(executables).ToList();

                foreach (var project in services)
                {
                    extensionConfiguration.Services.TryGetValue(project.Name, out DaprExtensionServiceConfiguration? serviceConfiguration);

                    if (serviceConfiguration?.Enabled != null && serviceConfiguration.Enabled.Value == false)
                    {
                        context.Output.WriteDebugLine($"Dapr has been disabled for service {project.Name}.");
                        continue;
                    }

                    var httpBinding = project.Bindings.FirstOrDefault(b => b.Protocol == "http");

                    if (httpBinding == null && project.Bindings.Count == 1 && project.Bindings[0].Protocol == null)
                    {
                        // Assume the only untyped binding is HTTP...
                        httpBinding = project.Bindings[0];
                    }

                    if (httpBinding == null && (serviceConfiguration?.Enabled == null || !serviceConfiguration.Enabled.Value))
                    {
                        context.Output.WriteDebugLine($"Dapr has been disabled for unbound service {project.Name}.");
                        continue;
                    }

                    // See https://github.com/dotnet/tye/issues/260
                    //
                    // Currently the pub-sub pattern does not work when you have multiple replicas. Each
                    // daprd instance expects that it has a single application to talk to. So if you're using
                    // pub-sub this means that you'll won't get some messages.
                    //
                    // We have no way to know if an app is using pub-sub or not, so just block it.
                    if (project.Replicas > 1)
                    {
                        throw new CommandException("Dapr support does not support multiple replicas yet for development.");
                    }

                    var daprExecutablePath = GetDaprExecutablePath();

                    var proxy = new ExecutableServiceBuilder($"{project.Name}-dapr", daprExecutablePath, ServiceSource.Extension)
                    {
                        WorkingDirectory = context.Application.Source.DirectoryName,

                        // These environment variables are replaced with environment variables
                        // defined for this service.
                        Args = $"run --app-id {project.Name} --dapr-grpc-port %DAPR_GRPC_PORT% --dapr-http-port %DAPR_HTTP_PORT% --metrics-port %METRICS_PORT%",
                    };

                    if (httpBinding != null)
                    {
                        proxy.Args += $" --app-port %APP_PORT%";
                    }

                    var daprPlacementPort = serviceConfiguration?.PlacementPort ?? extensionConfiguration.PlacementPort;

                    if (daprPlacementPort.HasValue)
                    {
                        context.Output.WriteDebugLine($"Using Dapr placement service host port {daprPlacementPort.Value} from 'placement-port' for service {project.Name}.");
                        proxy.Args += $" --placement-host-address localhost:{daprPlacementPort.Value}";
                    }

                    // When running locally `-config` specifies a filename, not a configuration name. By convention
                    // we'll assume the filename and config name are the same.
                    if (config.Data.TryGetValue("config", out var obj) && obj?.ToString() is string daprConfig)
                    {
                        var configFile = Path.Combine(context.Application.Source.DirectoryName!, "components", $"{daprConfig}.yaml");
                        if (File.Exists(configFile))
                        {
                            proxy.Args += $" --config \"{configFile}\"";
                        }
                        else
                        {
                            context.Output.WriteAlwaysLine($"Could not find dapr config '{configFile}'. Skipping...");
                        }
                    }

                    if (config.Data.TryGetValue("log-level", out obj) && obj?.ToString() is string logLevel)
                    {
                        proxy.Args += $" --log-level {logLevel}";
                    }

                    if (config.Data.TryGetValue("components-path", out obj) && obj?.ToString() is string componentsPath)
                    {
                        proxy.Args += $" --components-path {componentsPath}";
                    }
                    // Add dapr proxy as a service available to everyone.
                    proxy.Dependencies.UnionWith(context.Application.Services.Select(s => s.Name));

                    foreach (var s in context.Application.Services)
                    {
                        s.Dependencies.Add(proxy.Name);
                    }

                    context.Application.Services.Add(proxy);

                    // Listen for grpc on an auto-assigned port
                    var grpc = new BindingBuilder()
                    {
                        Name = "grpc",
                        Protocol = "https",
                    };
                    proxy.Bindings.Add(grpc);

                    // Listen for http on an auto-assigned port
                    var http = new BindingBuilder()
                    {
                        Name = "http",
                        Protocol = "http",
                    };
                    proxy.Bindings.Add(http);

                    // Listen for metrics on an auto-assigned port
                    var metrics = new BindingBuilder()
                    {
                        Name = "metrics",
                        Protocol = "http",
                    };
                    proxy.Bindings.Add(metrics);

                    if (httpBinding != null)
                    {
                        // Set APP_PORT based on the project's assigned port for http
                        var appPort = new EnvironmentVariableBuilder("APP_PORT")
                        {
                            Source = new EnvironmentVariableSourceBuilder(project.Name, binding: httpBinding.Name)
                            {
                                Kind = EnvironmentVariableSourceBuilder.SourceKind.Port,
                            },
                        };
                        proxy.EnvironmentVariables.Add(appPort);
                    }

                    // Set DAPR_GRPC_PORT based on this service's assigned port
                    var daprGrpcPort = new EnvironmentVariableBuilder("DAPR_GRPC_PORT")
                    {
                        Source = new EnvironmentVariableSourceBuilder(proxy.Name, binding: "grpc")
                        {
                            Kind = EnvironmentVariableSourceBuilder.SourceKind.Port,
                        },
                    };
                    proxy.EnvironmentVariables.Add(daprGrpcPort);

                    // Add another copy of this envvar to the project.
                    daprGrpcPort = new EnvironmentVariableBuilder("DAPR_GRPC_PORT")
                    {
                        Source = new EnvironmentVariableSourceBuilder(proxy.Name, binding: "grpc")
                        {
                            Kind = EnvironmentVariableSourceBuilder.SourceKind.Port,
                        },
                    };
                    project.EnvironmentVariables.Add(daprGrpcPort);

                    // Set DAPR_HTTP_PORT based on this service's assigned port
                    var daprHttpPort = new EnvironmentVariableBuilder("DAPR_HTTP_PORT")
                    {
                        Source = new EnvironmentVariableSourceBuilder(proxy.Name, binding: "http")
                        {
                            Kind = EnvironmentVariableSourceBuilder.SourceKind.Port,
                        },
                    };
                    proxy.EnvironmentVariables.Add(daprHttpPort);

                    // Add another copy of this envvar to the project.
                    daprHttpPort = new EnvironmentVariableBuilder("DAPR_HTTP_PORT")
                    {
                        Source = new EnvironmentVariableSourceBuilder(proxy.Name, binding: "http")
                        {
                            Kind = EnvironmentVariableSourceBuilder.SourceKind.Port,
                        },
                    };
                    project.EnvironmentVariables.Add(daprHttpPort);

                    // Set METRICS_PORT to a random port
                    var metricsPort = new EnvironmentVariableBuilder("METRICS_PORT")
                    {
                        Source = new EnvironmentVariableSourceBuilder(proxy.Name, binding: "metrics")
                        {
                            Kind = EnvironmentVariableSourceBuilder.SourceKind.Port,
                        },
                    };
                    proxy.EnvironmentVariables.Add(metricsPort);
                }
            }
            else
            {
                // In deployment, enumerate all projects and add anotations to each one.
                var projects = context.Application.Services.OfType<ProjectServiceBuilder>();
                foreach (var project in projects)
                {
                    // Dapr requires http. If this project isn't listening to HTTP then it's not daprized.
                    var httpBinding = project.Bindings.FirstOrDefault(b => b.Protocol == "http");
                    if (httpBinding == null)
                    {
                        continue;
                    }

                    if (!(project.ManifestInfo?.Deployment is { } deployment))
                    {
                        continue;
                    }

                    deployment.Annotations.Add("dapr.io/enabled", "true");
                    deployment.Annotations.Add("dapr.io/app-id", project.Name);
                    deployment.Annotations.Add("dapr.io/app-port", (httpBinding.Port ?? 80).ToString(CultureInfo.InvariantCulture));

                    if (config.Data.TryGetValue("config", out var obj) && obj?.ToString() is string daprConfig)
                    {
                        deployment.Annotations.TryAdd("dapr.io/config", daprConfig);
                    }

                    if (config.Data.TryGetValue("log-level", out obj) && obj?.ToString() is string logLevel)
                    {
                        deployment.Annotations.TryAdd("dapr.io/log-level", logLevel);
                    }
                }
            }
        }

        private static Task VerifyDaprInitialized(ExtensionContext context)
        {
            return Task.Run(
                () =>
                {
                    string? stdout = null;

                    try
                    {
                        ProcessExtensions.RunProcessAndWaitForExit("dapr", "--version", TimeSpan.FromSeconds(10), out stdout);
                    }
                    catch
                    {
                    }

                    if (stdout != null)
                    {
                        var match = Regex.Match(stdout, "^Runtime version: (?<version>.+)$", RegexOptions.Multiline);

                        if (match.Success)
                        {
                            if (match.Groups["version"].Value == "n/a")
                            {
                                throw new CommandException("Dapr has not been initialized (e.g. via `dapr init`).");
                            }
                            else
                            {
                                // Some version of Dapr has been initialized...
                                return;
                            }
                        }
                    }

                    context.Output.WriteAlwaysLine($"Unable to determine whether Dapr has been installed and initialized (e.g. via `dapr init`).");
                });
        }

        private string GetDaprExecutablePath()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // The Dapr Windows installation script defaults to "C:\dapr".
                var windowsPath = Environment.ExpandEnvironmentVariables("C:/dapr/dapr.exe");
                if (File.Exists(windowsPath))
                {
                    return windowsPath;
                }
            }
            else
            {
                var nixpath = Environment.ExpandEnvironmentVariables("/usr/local/bin/dapr");
                if (File.Exists(nixpath))
                {
                    return nixpath;
                }
            }

            // Dapr is usually on the path.
            return "dapr";
        }
    }
}
