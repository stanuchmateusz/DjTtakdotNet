using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DjTtakdotNet.Services;
using Serilog;
using System.Diagnostics;
using DjTtakdotNet.Music;

namespace DjTtakdotNet.Modules;

public class MusicModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly MusicService _musicService;
    private readonly QueueService _queueService;
    private const int DELATE_TIME_IN_MS = 1000 * 120;
    public MusicModule(MusicService musicService, QueueService queueService)
    {
        _musicService = musicService;
        _queueService = queueService;
        AppDomain.CurrentDomain.ProcessExit += OnShutdown;
        Console.CancelKeyPress += OnShutdown;
    }

    [SlashCommand("stop", "Zatrzymaj bieżące odtwarzanie")]
    public async Task StopPlayback()
    {
        await _musicService.DisconnectAsync();
        await RespondAsync("Zatrzymano odtwarzanie!");
        await Task.Delay(DELATE_TIME_IN_MS);
        await DeleteOriginalResponseAsync();
    }

    [SlashCommand("play", "Odtwórz utwór z YouTube")]
    public async Task PlayCommand(string query)
    {
        await DeferAsync();
        try
        {
            var user = Context.User as IGuildUser;
            var voiceChannel = user?.VoiceChannel;

            if (voiceChannel == null)
            {
                var followup = await FollowupAsync("❌ Musisz być na kanale głosowym!");
                _ = DeleteAfterDelay(followup);
                return;
            }

            var trackInfo = await MusicService.GetTrackInfoAsync(query);

            if (!_musicService.IsConnected || _musicService.currentChannel?.Id != voiceChannel.Id)
            {
                await _musicService.DisconnectAsync();
                await _musicService.JoinChannelAsync(voiceChannel);
            }

            bool isNowPlaying = await _musicService.PlayAsync(trackInfo);

            if (isNowPlaying)
            {
                var embed = BuildNowPlayingEmbed(trackInfo);
                var followup = await FollowupAsync(embed: embed.Build());
                _ = DeleteAfterDelay(followup);
            }
            else
            {
                var embed = new EmbedBuilder()
                    .WithDescription($"✅ Dodano do kolejki: [{trackInfo.Title}]({trackInfo.Url})")
                    .WithColor(Color.Green)
                    .Build();
                var followup = await FollowupAsync(embed: embed);
                _ = DeleteAfterDelay(followup);
            }
        }
        catch (TrackNotFoundException ex)
        {
            var followup = await FollowupAsync("❌ Nie udało znaleźć się wyszukiwanej frazy");
            _ = DeleteAfterDelay(followup);
        }
        catch (Exception ex)
        {
            var followup = await FollowupAsync($"❌ Błąd: {ex.Message}");
            _ = DeleteAfterDelay(followup);
        }
    }

    [SlashCommand("queue", "Wyświetl kolejkę odtwarzania")]
    public async Task ShowQueue()
    {
        var queue = _queueService.GetQueue().ToArray();
        var current = _queueService.CurrentTrack;

        var embed = new EmbedBuilder()
            .WithTitle("🎶 Kolejka odtwarzania")
            .WithColor(Color.Blue);

        if (current != null)
        {
            embed.AddField("Teraz gra:", $"[{current.Title}]({current.Url}) ({current.DurationString})");
        }

        if (queue.Any())
        {
            var queueText = string.Join("\n", queue.Select((t, i) =>
                $"{i + 1}. [{t.Title.Truncate(30)}]({t.Url}) ({t.DurationString})"));

            embed.AddField("Kolejka:", queueText);
        }
        else
        {
            embed.WithDescription("Kolejka jest pusta!");
        }

        embed.WithFooter($"Tryb powtarzania: {_queueService.CurrentLoopMode}");
        await RespondAsync(embed: embed.Build());
        await Task.Delay(DELATE_TIME_IN_MS);
        await DeleteOriginalResponseAsync();
    }

    [SlashCommand("nowplaying", "Pokaż obecnie grany utwór")]
    public async Task NowPlaying()
    {
        var track = _queueService.CurrentTrack;

        if (track == null)
        {
            await RespondAsync("Nic nie jest teraz odtwarzane!");
            return;
        }

        var embed = BuildNowPlayingEmbed(track)
            .AddField("Pozycja w kolejce", $"#{_queueService.GetQueue().Count() + 1}")
            .WithFooter($"ID: {track.Id}");

        await RespondAsync(embed: embed.Build());
        await Task.Delay(DELATE_TIME_IN_MS);
        await DeleteOriginalResponseAsync();
    }

    [SlashCommand("remove", "Usuń utwór z kolejki")]
    public async Task RemoveTrack(string trackId)
    {
        if (!Guid.TryParse(trackId, out var guid))
        {
            await RespondAsync("Nieprawidłowe ID utworu!");
            await Task.Delay(DELATE_TIME_IN_MS);
            await DeleteOriginalResponseAsync();
            return;
        }

        var removed = _queueService.RemoveTrack(guid);
        await RespondAsync(removed ? "Utwór usunięty z kolejki!" : "Nie znaleziono utworu w kolejce!");
        await Task.Delay(DELATE_TIME_IN_MS);
        await DeleteOriginalResponseAsync();
    }

    [SlashCommand("skip", "Pomiń obecny utwór")]
    public async Task SkipTrack()
    {
        _musicService.StopCurrentPlayback();
        await RespondAsync("Pomijam obecny utwór...");
        await Task.Delay(DELATE_TIME_IN_MS);
        await DeleteOriginalResponseAsync();
    }

    [SlashCommand("loop", "Zmień tryb powtarzania")]
    public async Task ToggleLoop()
    {
        var newMode = _queueService.ToggleLoop();
        var modeText = newMode switch
        {
            QueueService.LoopMode.None => "Wyłączone",
            QueueService.LoopMode.Single => "Powtarzanie utworu",
            QueueService.LoopMode.All => "Powtarzanie kolejki",
            _ => throw new ArgumentOutOfRangeException()
        };

        await RespondAsync($"Tryb powtarzania: **{modeText}**");
        await Task.Delay(DELATE_TIME_IN_MS);
        await DeleteOriginalResponseAsync();
    }

    private EmbedBuilder BuildNowPlayingEmbed(TrackInfo trackInfo)
    {
        var embed = new EmbedBuilder()
            .WithTitle("🎶 Teraz odtwarzam")
            .WithDescription($"[{trackInfo.Title}]({trackInfo.Url})")
            .WithColor(new Color(0x1DB954))
            .WithThumbnailUrl(trackInfo.Thumbnail)
            .AddField("Autor", trackInfo.Uploader, true)
            .AddField("Czas trwania", trackInfo.Duration.ToString(@"mm\:ss"), true)
            .AddField("Wyszukiwana fraza", $"`{trackInfo.Query.Truncate(50)}`")
            .WithFooter(f => f.Text = $"Żądane przez {Context.User.Username}")
            .WithCurrentTimestamp();

        return embed;
    }

    private void OnShutdown(object? sender, EventArgs e)
    {
        _musicService.DisconnectAsync().GetAwaiter().GetResult();
    }
    
    private static async Task DeleteAfterDelay(IUserMessage message)
    {
        await Task.Delay(DELATE_TIME_IN_MS);
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