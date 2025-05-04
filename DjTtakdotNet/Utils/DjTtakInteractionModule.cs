using Discord;
using Discord.Interactions;

namespace DjTtakdotNet.Utils;

public class DjTtakInteractionModule : InteractionModuleBase<SocketInteractionContext>
{
    protected IDjTtakConfig DjTtakDjTtakConfig { get; set; }
    protected DjTtakInteractionModule(IDjTtakConfig djTtakConfig)
    {
        DjTtakDjTtakConfig = djTtakConfig;
    }
    
    protected async Task RespondAndDispose( string text = null, 
        Embed[] embeds = null, 
        bool isTTS = false, 
        bool ephemeral = false, 
        AllowedMentions allowedMentions = null, 
        RequestOptions options = null, 
        MessageComponent components = null, 
        Embed embed = null, 
        PollProperties poll = null)
    { 
        await RespondAsync(embeds:embeds, isTTS: isTTS, ephemeral: ephemeral, allowedMentions: allowedMentions, options: options, components: components, embed: embed, poll: poll, text: text );
        if (DjTtakDjTtakConfig.DeleteMessageTimeout == 0)
            return;
        await Task.Delay(DjTtakDjTtakConfig.DeleteMessageTimeout);
        await DeleteOriginalResponseAsync();
    }
}