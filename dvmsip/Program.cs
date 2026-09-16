// SPDX-License-Identifier: AGPL-3.0-or-later
/**
 * Digital Voice Modem - SIP Telephone Interconnect
 * AGPLv3 Open Source. Use is subject to license terms.
 *
 *   Copyright (C) 2024 DVMProject Contributors
 *
 */

using dvmsip.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace dvmsip
{
    internal static class Program
    {
        public const string Version = "01A00";

        static async Task<int> Main(string[] args)
        {
            string configPath = "dvmsip-config.yml";

            // Parse -c / --config argument
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-c" || args[i] == "--config")
                    configPath = args[i + 1];
            }

            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"dvmsip: configuration file not found: {configPath}");
                return 1;
            }

            DvmSipConfig config;
            try
            {
                config = DvmSipConfig.LoadFromFile(configPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"dvmsip: failed to parse configuration: {ex.Message}");
                return 1;
            }

            ConfigureSerilog(config);

            Log.Information("dvmsip R{Version} - DVM SIP Telephone Interconnect", Version);
            Log.Information("Loading configuration from {Path}", configPath);

            try
            {
                await Host.CreateDefaultBuilder(args)
                    .UseSerilog()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(config);
                        services.AddHostedService<HostService>();
                    })
                    .RunConsoleAsync();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Host terminated unexpectedly");
                return 1;
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }

            return 0;
        }

        private static void ConfigureSerilog(DvmSipConfig config)
        {
            var consoleLevel = DvmLevelToSerilog(config.Log.DisplayLevel);
            var fileLevel    = DvmLevelToSerilog(config.Log.FileLevel);

            const string template = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .WriteTo.Console(restrictedToMinimumLevel: consoleLevel, outputTemplate: template)
                .WriteTo.File(
                    config.Log.FilePath,
                    restrictedToMinimumLevel: fileLevel,
                    outputTemplate: template,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .CreateLogger();
        }

        private static LogEventLevel DvmLevelToSerilog(int level) => level switch
        {
            0 => LogEventLevel.Fatal,   // none — only fatal passes through
            1 => LogEventLevel.Information,
            2 => LogEventLevel.Debug,
            _ => LogEventLevel.Verbose  // 3 = trace
        };
    }
}
