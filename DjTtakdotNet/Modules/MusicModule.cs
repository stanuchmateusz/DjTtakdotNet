using Discord;
using Discord.Interactions;
using DjTtakdotNet.Services;
using Serilog;
using DjTtakdotNet.Music;
using DjTtakdotNet.Utils;

namespace DjTtakdotNet.Modules;

public class MusicModule : DjTtakInteractionModule
{
    private readonly MusicService _musicService;
    private readonly QueueService _queueService;
    private const short MaxTracksPerPage = 10;
    public MusicModule(MusicService musicService, QueueService queueService, IDjTtakConfig config) : base (config)
    {
        _musicService = musicService;
        _queueService = queueService;
        AppDomain.CurrentDomain.ProcessExit += OnShutdown;
        Console.CancelKeyPress += OnShutdown;
    }
    
   
    [SlashCommand("stop", "Stop current playback and leave that channel")]
    public async Task StopPlayback()
    {
        await _musicService.Disconnect();
        await RespondAndDispose("Stopped playback and left the channel");
    }

    [SlashCommand("play", "Play from URL or serach and play from YouTube")]
    public async Task PlayCommand(string query)
    {
        //todo add support for youtube playlists
        await DeferAsync();
        try
        {
            var user = Context.User as IGuildUser;
            var voiceChannel = user?.VoiceChannel;
            if (voiceChannel == null)
            {
                var followup = await FollowupAsync("❌ You have to be on a VoiceChannel!");
                _ = DeleteAfterDelay(followup);
                return;
            }

            if (_musicService.IsConnected && voiceChannel.GuildId != _musicService.CurrentChannel?.GuildId)
            {
                var followup = await FollowupAsync("❌ DJ is already on different voice channel!");
                _ = DeleteAfterDelay(followup);
                return;
            }
            
            var trackInfo = await MusicService.GetTrackInfoAsync(query);
            
            if (!_musicService.IsConnected)
            {
                await _musicService.Disconnect();
                await _musicService.JoinChannelAsync(voiceChannel);
            }

            var isNowPlaying = _musicService.StartQueue(trackInfo);
            
            var embed = new EmbedBuilder();
            if (isNowPlaying)
            {
                 embed = BuildNowPlayingEmbed(embed, trackInfo);
            }
            else
            {
                embed
                    .WithDescription($"✅ Added to queue: [{trackInfo.Title}]({trackInfo.Url})")
                    .WithColor(Color.Green);
            }
            var response = await FollowupAsync(embed: embed.Build());
            _ = DeleteAfterDelay(response);
        }
        catch (TrackNotFoundException)
        {
            var followup = await FollowupAsync("❌ Failed to find requested track!");
            _ = DeleteAfterDelay(followup);
        }
        catch (Exception ex)
        {
            Log.Error(exception:ex, "Error during playback");
            var followup = await FollowupAsync($"❌ There was an error during playback, please try again later.");
            _ = DeleteAfterDelay(followup);
        }
    }

    [SlashCommand("queue", "Print the queue")]
    public async Task ShowQueue(int page = 1)
    {
        var queue = _queueService.GetQueue().ToArray();
        var current = _queueService.CurrentTrack;

        var embed = new EmbedBuilder()
            .WithColor(Color.Blue);

        if (current != null)
        {
            embed.AddField("Now playing:", $"[{current.Title}]({current.Url}) ({current.DurationString})");
        }

        var totalPages = (queue.Length + 9) / MaxTracksPerPage;
        page = Math.Clamp(page, 1, totalPages == 0 ? 1 : totalPages);

        if (queue.Length > 0)
        {
            var pagedQueue = queue
                .Skip((page - 1) * MaxTracksPerPage)
                .Take(MaxTracksPerPage)
                .ToArray();

            var queueText = string.Join("\n", pagedQueue.Select((t, index) =>
            {
                var position = (page - 1) * MaxTracksPerPage + index + 1;
                return $"{position}. [{t.Title.Truncate(30)}]({t.Url}) ({t.DurationString})";
            }));

            embed.AddField("Queue:", queueText);

            embed.WithFooter(totalPages > 1
                ? $"Page {page}/{totalPages} | Loop mode: {_queueService.CurrentLoopMode}"
                : $"Loop mode: {_queueService.CurrentLoopMode}");

            embed.WithTitle($"🎶 Queue (Page {page}/{totalPages})");
        }
        else
        {
            embed.WithDescription("Queue is empty!");
            embed.WithFooter($"Loop mode: {_queueService.CurrentLoopMode}");
        }

        await RespondAndDispose(embed: embed.Build());
    }

    [SlashCommand("nowplaying", "Show info about currently playing")]
    public async Task NowPlaying()
    {
        var track = _queueService.CurrentTrack;

        if (track == null)
        {
            await RespondAsync("There's no current playing track!");
            return;
        }

        var embed = BuildNowPlayingEmbed(new EmbedBuilder(), track)
            .AddField("Position in queue", $"#{_queueService.GetQueue().Count() + 1}");
        
        await RespondAndDispose(embed: embed.Build());
    }

    [SlashCommand("remove", "Remove track from queue")]
    public async Task RemoveTrack(string trackId)
    {   
        if (!int.TryParse(trackId, out var id))
        {
            await RespondAndDispose("Invalid track ID!");
            return;
        }
        var removed = _queueService.RemoveTrack(id - 1);
        await RespondAndDispose(removed ? "Track removed from the queue!" : "Could not remove track!");
    }

    [SlashCommand("skip", "Skip current track")]
    public async Task SkipTrack()
    {
        _musicService.StopCurrentPlayback();
        await RespondAndDispose("Skipping current track");
    }

    [SlashCommand("loop", "Change the loop mode")]
    public async Task ToggleLoop(QueueService.LoopMode mode)
    {
        var newMode = _queueService.CurrentLoopMode = mode;
        var modeText = newMode switch
        {
            QueueService.LoopMode.None => "OFF",
            QueueService.LoopMode.Single => "Single track loop",
            QueueService.LoopMode.All => "Queue loop",
            _ => throw new ArgumentOutOfRangeException()
        };

        await RespondAndDispose($"Loop mode: **{modeText}**");
    }

    [SlashCommand("clear", "Clears the queue")]
    public async Task ClearTrack()
    {
        _queueService.ClearQueue();
        await RespondAndDispose("Queue cleared!");
    }

    private EmbedBuilder BuildNowPlayingEmbed(EmbedBuilder builder, TrackInfo trackInfo)
    {
        return builder
            .WithTitle("🎶 Now playing")
            .WithDescription($"[{trackInfo.Title}]({trackInfo.Url})")
            .WithColor(new Color(0x1DB954))
            .WithThumbnailUrl(trackInfo.Thumbnail)
            .AddField("By:", trackInfo.Uploader, true)
            .AddField("Length:", trackInfo.Duration.ToString(@"mm\:ss"), true)
            .AddField("Search phrase:", $"`{trackInfo.Query.Truncate(50)}`")
            .WithFooter(f => f.Text = $"Requested by: {Context.User.Username}")
            .WithCurrentTimestamp();
    }

    private void OnShutdown(object? sender, EventArgs e)
    {
        _musicService.Disconnect().GetAwaiter().GetResult();
    }
    
    private async Task DeleteAfterDelay(IUserMessage message)
    {
        await Task.Delay(DjTtakDjTtakConfig.DeleteMessageTimeout);
        try
        {
            await message.DeleteAsync();
        }
        catch
        {
            // ignore - message removed etc
        }
    }
}

public static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}