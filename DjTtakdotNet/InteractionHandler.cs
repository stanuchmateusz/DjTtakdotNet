namespace DjTtakdotNet;

using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using System;
using System.Reflection;
using System.Threading.Tasks;

public class InteractionHandler
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _handler;
    private readonly IServiceProvider _services;

    public InteractionHandler(DiscordSocketClient client, InteractionService handler, IServiceProvider services)
    {
        _client = client;
        _handler = handler;
        _services = services;
    }

    public async Task InitializeAsync()
    {
        _client.Ready += ReadyAsync;
        
        await _handler.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
        
        _client.InteractionCreated += HandleInteraction;
        _handler.InteractionExecuted += HandleInteractionExecute;
    }
    
    private async Task ReadyAsync()
    {
        foreach (var clientGuild in _client.Guilds)
        {
            await _handler.RegisterCommandsToGuildAsync(clientGuild.Id);
        }
        // await _handler.RegisterCommandsGloballyAsync();
    }

    private async Task HandleInteraction(SocketInteraction interaction)
    {
        try
        {

            var context = new SocketInteractionContext(_client, interaction);
            
            var result = await _handler.ExecuteCommandAsync(context, _services);
                
            if (!result.IsSuccess)
                switch (result.Error)
                {
                    case InteractionCommandError.UnmetPrecondition:
                        await interaction.Channel.SendMessageAsync(result.ErrorReason);
                        break;
                    default:
                        //ignore
                        break;
                }
        }
        catch
        {
            if (interaction.Type is InteractionType.ApplicationCommand)
                await interaction.GetOriginalResponseAsync().ContinueWith(async (msg) => await msg.Result.DeleteAsync());
        }
    }

    private static Task HandleInteractionExecute(ICommandInfo commandInfo, IInteractionContext context, IResult result)
    {
        if (!result.IsSuccess)
            switch (result.Error)
            {
                case InteractionCommandError.UnmetPrecondition:
                    // implement
                    context.Interaction.RespondAsync(result.ErrorReason);
                    break;
                default:
                    break;
            }

        return Task.CompletedTask;
    }
}