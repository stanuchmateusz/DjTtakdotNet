using Discord.Interactions;
using DjTtakdotNet.Utils;

namespace DjTtakdotNet.Modules;

public class HelpModule(IDjTtakConfig djTtakConfig) : DjTtakInteractionModule(djTtakConfig)
{
    [SlashCommand("help", "Displays help for a command")]
    public async Task HelpAsync()
    {
        await RespondAndDispose("There are currently no commands available.");
    }
    
}