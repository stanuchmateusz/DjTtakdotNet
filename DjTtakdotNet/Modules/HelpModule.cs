using Discord.Interactions;
using DjTtakdotNet.Utils;

namespace DjTtakdotNet.Modules;

public class HelpModule(IDjTtakConfig djTtakConfig) : DjTtakInteractionModule(djTtakConfig)
{
    [SlashCommand("help", "Displays help")]
    public async Task HelpAsync()
    {
        await RespondAsync("If you have any problems with playback, make sure that yt-dlp is up to date. (you can update it with yt-dlp -U)");
    }
    
}