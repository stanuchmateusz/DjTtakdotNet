using System.Globalization;
using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DjTtakdotNet.Services;
using DjTtakdotNet.Utils;
using Serilog;
using Serilog.Events;

namespace DjTtakdotNet;

static class Program
{
    private static DiscordSocketClient Client;
    private static readonly DiscordSocketConfig SocketConfig = new()
    {
        GatewayIntents = GatewayIntents.Guilds 
                         | GatewayIntents.GuildVoiceStates 
                         | GatewayIntents.GuildMessages
                         | GatewayIntents.GuildMembers,
        AlwaysDownloadUsers = true,
        MessageCacheSize = 100,
        LogLevel = LogSeverity.Info
    };

    private static readonly InteractionServiceConfig InteractionServiceConfig = new()
    {
        //todo not implemented
        LocalizationManager = new ResxLocalizationManager("DjTtakdotNet.Resources.DjTtakLocales", Assembly.GetEntryAssembly(),
            new CultureInfo("en-US"), new CultureInfo("pl"))
    };


    public static async Task Main(string[] args)
    {
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .CreateLogger();

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false)
                .Build();
            
            var djTtakConfig = new DjTtakConfig(configuration);
            
            var services = new ServiceCollection()
                .AddSingleton<IDjTtakConfig>(djTtakConfig)
                .AddSingleton(SocketConfig)
                .AddSingleton<DiscordSocketClient>()
                .AddSingleton<MusicService>()
                .AddSingleton<QueueService>()
                .AddSingleton(x =>
                    new InteractionService(x.GetRequiredService<DiscordSocketClient>(), InteractionServiceConfig))
                .AddSingleton<InteractionHandler>()
                .BuildServiceProvider();

            Client = services.GetRequiredService<DiscordSocketClient>();

            Client.Log += LogAsync;
            Client.Ready += async () =>
            {
                Log.Information("Bot is ready {0}",Client.CurrentUser.Username);
                await Client.SetActivityAsync(new Game("Music 🎵"));
            };
            await services.GetRequiredService<InteractionHandler>()
                .InitializeAsync();
            
            await Client.LoginAsync(TokenType.Bot, djTtakConfig.Token);
            await Client.StartAsync();

            await Task.Delay(Timeout.Infinite);
        }
        finally
        {
            await Client.StopAsync();
        }
    }
    
    private static async Task LogAsync(LogMessage message)
    {
        var severity = message.Severity switch
        {
            LogSeverity.Critical => LogEventLevel.Fatal,
            LogSeverity.Error => LogEventLevel.Error,
            LogSeverity.Warning => LogEventLevel.Warning,
            LogSeverity.Info => LogEventLevel.Information,
            LogSeverity.Verbose => LogEventLevel.Verbose,
            LogSeverity.Debug => LogEventLevel.Debug,
            _ => LogEventLevel.Information
        };
        Log.Write(severity, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        await Task.CompletedTask;
    }
}