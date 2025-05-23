﻿using Discord;
using Discord.Interactions;
using DjTtakdotNet.Music;
using DjTtakdotNet.Services;
using DjTtakdotNet.Utils;
using Serilog;

namespace DjTtakdotNet.Modules;

public class MusicModule : DjTtakInteractionModule
{
    private const short MaxTracksPerPage = 10;
    private readonly MusicService _musicService;
    private readonly QueueService _queueService;

    public MusicModule(MusicService musicService, QueueService queueService, IDjTtakConfig config) : base(config)
    {
        _musicService = musicService;
        _queueService = queueService;
        AppDomain.CurrentDomain.ProcessExit += OnShutdown;
        Console.CancelKeyPress += OnShutdown;
    }


    [SlashCommand("stop", "Stops current playback and leaves voice channel")]
    [RequireContext(ContextType.Guild)]
    public async Task StopPlayback()
    {
        await _musicService.DisconnectAsync();
        await RespondAndDispose("Stopped playback and left the channel");
    }

    [SlashCommand("play", "Play from URL or serach and play from YouTube")]
    [RequireContext(ContextType.Guild)]
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
                var followup = await FollowupAsync("❌ DJ is already on different VoiceChannel!");
                _ = DeleteAfterDelay(followup);
                return;
            }

            var trackInfo = await MusicService.GetTrackInfoAsync(query);

            if (!_musicService.IsConnected || _musicService.CurrentChannel == null ||
                _musicService.CurrentChannel.Id == voiceChannel.Id) await _musicService.JoinChannelAsync(voiceChannel);

            var isNowPlaying = _musicService.StartQueue(trackInfo);

            var embed = new EmbedBuilder();
            if (isNowPlaying)
                embed = BuildNowPlayingEmbed(embed, trackInfo);
            else
                embed
                    .WithDescription($"✅ Added to queue: [{trackInfo.Title}]({trackInfo.Url})")
                    .WithColor(Color.Green);
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
            Log.Error(ex, "Error during playback");
            var followup = await FollowupAsync("\u274c There was an error during playback, please try again later.");
            _ = DeleteAfterDelay(followup);
        }
    }

    [SlashCommand("queue", "Shows the queue")]
    [RequireContext(ContextType.Guild)]
    public async Task ShowQueue(int page = 1)
    {
        var queue = _queueService.GetQueue().ToArray();
        var current = _queueService.CurrentTrack;

        var embed = new EmbedBuilder()
            .WithColor(Color.Blue);

        if (current != null)
            embed.AddField("Now playing:", $"[{current.Title}]({current.Url}) ({current.DurationString})");

        var totalPages = (queue.Length + 9) / MaxTracksPerPage;
        totalPages = Math.Max(1, totalPages);
        page = Math.Clamp(page, 1, totalPages);

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

            embed.WithTitle("\ud83c\udfb6 Queue");
        }
        else
        {
            embed.WithDescription("Queue is empty!");
            embed.WithFooter($"Loop mode: {_queueService.CurrentLoopMode}");
            embed.WithTitle("🎶 Queue");
        }

        await RespondAndDispose(embed: embed.Build());
    }

    [SlashCommand("nowplaying", "Shows info about track that's currently playing")]
    [RequireContext(ContextType.Guild)]
    public async Task NowPlaying()
    {
        var track = _queueService.CurrentTrack;

        if (track == null)
        {
            await RespondAsync("There's no current playing track!");
            return;
        }

        var queueCount = _queueService.GetQueue().Count();
        var positionText = queueCount > 0 ? $"#{queueCount + 1} (Current + {queueCount} in queue)" : "#1 (Current)";

        var embed = BuildNowPlayingEmbed(new EmbedBuilder(), track)
            .AddField("Position in queue", positionText);

        await RespondAndDispose(embed: embed.Build());
    }

    [SlashCommand("remove", "Removes track from the queue")]
    [RequireContext(ContextType.Guild)]
    public async Task RemoveTrack(string trackId)
    {
        if (!int.TryParse(trackId, out var id) || id <= 0)
        {
            await RespondAndDispose("Invalid track ID!");
            return;
        }

        var removed = _queueService.RemoveTrack(id - 1);
        await RespondAndDispose(removed
            ? $"Track #{id} removed from the queue!"
            : $"Could not remove track #{id}. It might not exist.");
    }

    [SlashCommand("skip", "Skips current track")]
    [RequireContext(ContextType.Guild)]
    public async Task SkipTrack()
    {
        if (_queueService.CurrentTrack == null && _queueService.IsIdle())
        {
            await RespondAndDispose("There is no track currently playing or in the queue to skip.");
            return;
        }

        _musicService.StopCurrentPlayback();
        await RespondAndDispose("Skipping current track...");
    }

    [SlashCommand("loop", "Changes the loop mode of the queue")]
    [RequireContext(ContextType.Guild)]
    public async Task ToggleLoop(QueueService.LoopMode mode)
    {
        var newMode = _queueService.CurrentLoopMode = mode;
        var modeText = newMode switch
        {
            QueueService.LoopMode.None => "OFF",
            QueueService.LoopMode.Single => "Single track loop",
            QueueService.LoopMode.All => "Queue loop",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), @"Invalid loop mode")
        };

        await RespondAndDispose($"Loop mode: **{modeText}**");
    }

    [SlashCommand("next", "Plays the next track in the queue")]
    [RequireContext(ContextType.Guild)]
    public async Task NextTrack()
    {
        if (_queueService.CurrentTrack == null && _queueService.IsIdle())
        {
            await RespondAndDispose(
                "There is no track currently playing or in the queue to advance from."); // Slightly adjusted message
            return;
        }

        _musicService.StopCurrentPlayback(true);
        await RespondAndDispose("Playing next track...");
    }

    [SlashCommand("clear", "Clears the queue")]
    [RequireContext(ContextType.Guild)]
    public async Task ClearTrack()
    {
        if (!_queueService.GetQueue().Any() && _queueService.CurrentTrack == null)
        {
            await RespondAndDispose("The queue is already empty!");
            return;
        }

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
        Log.Information("Shutdown signal received. Disconnecting music service.");
        _musicService.DisconnectAsync().GetAwaiter().GetResult();
        Log.Information("Music service disconnected during shutdown.");
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