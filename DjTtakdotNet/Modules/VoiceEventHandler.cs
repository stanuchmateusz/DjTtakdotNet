using Discord;
using Discord.WebSocket;
using DjTtakdotNet.Services;
using Serilog;

namespace DjTtakdotNet.Modules;

public class VoiceEventHandler(DiscordSocketClient client, MusicService musicService)
{
    public async Task InitializeAsync()
    {
        client.UserVoiceStateUpdated += OnUserVoiceStateUpdatedAsync;
    }

    private async Task OnUserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState oldState,
        SocketVoiceState newState)
    {
        if (user.IsBot) return;
        var botCurrentChannel = musicService.CurrentChannel;

        if (botCurrentChannel == null) return;

        if (oldState.VoiceChannel != null && oldState.VoiceChannel.Id == botCurrentChannel.Id &&
            (newState.VoiceChannel == null || newState.VoiceChannel.Id != botCurrentChannel.Id))
        {
            Log.Information(
                "User {UserName} left {ChannelName}. Checking if bot is alone in the channel.",
                user.Username, oldState.VoiceChannel.Name);

            await Task.Delay(TimeSpan.FromSeconds(1));

            if (musicService.CurrentChannel == null || musicService.CurrentChannel.Id != oldState.VoiceChannel.Id)
            {
                Log.Debug(
                    "Bot is no longer in {ChannelName} or channel has changed. Aborting check.",
                    oldState.VoiceChannel.Name);
                return;
            }

            try
            {
                var usersInChannel = await botCurrentChannel.GetUsersAsync().FlattenAsync().ConfigureAwait(false);
                var nonBotUsersCount = usersInChannel.Where(guildUser => guildUser.VoiceChannel == botCurrentChannel) .Count(u => !u.IsBot);

                if (nonBotUsersCount == 0)
                {
                    Log.Information("Bot is alone {ChannelName}. Disconnecting.", botCurrentChannel.Name);
                    await musicService.DisconnectAsync().ConfigureAwait(false);
                    musicService.StopCurrentPlayback();
                }
                else
                {
                    Log.Debug("Bot is no longer alone in {ChannelName}. {Count} non bots left.",
                        botCurrentChannel.Name, nonBotUsersCount);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "There was an error checking if bot is alone in {ChannelName}.", botCurrentChannel.Name);
            }
        }
    }
}