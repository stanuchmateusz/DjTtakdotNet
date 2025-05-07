namespace DjTtakdotNet;

using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System;
using System.Reflection;
using System.Threading.Tasks;

public class InteractionHandler(DiscordSocketClient client, InteractionService handler, IServiceProvider services)
{
    public async Task InitializeAsync()
    {
        client.Ready += ReadyAsync;
        
        await handler.AddModulesAsync(Assembly.GetEntryAssembly(), services);
        
        client.InteractionCreated += HandleInteraction;
        handler.InteractionExecuted += HandleInteractionExecute;
    }
    
    private async Task ReadyAsync()
    {
        foreach (var clientGuild in client.Guilds)
        {
            await handler.RegisterCommandsToGuildAsync(clientGuild.Id);
        }
        await handler.RegisterCommandsGloballyAsync();
    }

    private async Task HandleInteraction(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(client, interaction);
            
            var result = await handler.ExecuteCommandAsync(context, services);
                
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