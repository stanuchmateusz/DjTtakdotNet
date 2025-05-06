using Discord.Audio;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Timers;
using CliWrap;
using Discord;
using DjTtakdotNet.Music;
using Serilog;
using Timer = System.Timers.Timer;

namespace DjTtakdotNet.Services;

public class MusicService : IDisposable
{
    private IAudioClient? _audioClient;
    private CancellationTokenSource _cts;
    private Timer _inactivityTimer;
    private DateTime _lastActivity;
    public IVoiceChannel? CurrentChannel { get; private set; }
    private readonly QueueService _queueService;
    private bool _isProcessingQueue;


    public MusicService( QueueService queueService)
    {
        _queueService = queueService;
        _cts = new CancellationTokenSource();
    }

    public async Task JoinChannelAsync(IVoiceChannel channel)
    {
        if (_audioClient != null)
            throw new InvalidOperationException("Dj is already connected!");

        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
            var connectTask = channel.ConnectAsync(selfDeaf: false);

            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            if (completedTask == timeoutTask)
                throw new TimeoutException("Voice channel connection timed out!");

            _audioClient = await connectTask;
            CurrentChannel = channel;
            _lastActivity = DateTime.Now;
            StartInactivityTimer();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while joining channel");
            throw new InvalidOperationException("Error while joining channel");
        }
    }


    public bool StartQueue(TrackInfo track)
    {
        var wasIdle = _queueService.IsIdle();
        _queueService.AddToQueue(track);
        Log.Debug("Added {0} to then queue",track.Title);

        if (!wasIdle) return false;
        if (_isProcessingQueue) return true;
        
        _isProcessingQueue = true;
        _ = ProcessQueueAsync();
        return true;
    }


    private async Task ProcessQueueAsync()
    {
        try
        {
            while (!_queueService.IsIdle())
            {
                var track = _queueService.GetNextTrack();
                if (track == null)
                {
                    await Task.Delay(1000);
                    if (_queueService.IsIdle()) break;
                    continue;
                }
                Log.Debug("PlayTrack {0}", track.Title);
                try
                {
                    await PlayTrackAsync(track);
                }
                catch (InvalidOperationException)
                {
                    //no voice connection
                    try
                    {
                        if (_queueService.IsIdle()) break;
                        if (CurrentChannel == null) break;
                        await JoinChannelAsync(CurrentChannel);
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                }
                catch (OperationCanceledException)
                {
                    // canceled on purpose
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unknown error while playing track");
                    break;
                }
            }
        }
        finally
        {
            _queueService.ClearQueue();
            _isProcessingQueue = false;
        }
    }


    private async Task PlayTrackAsync(TrackInfo track)
{
    if (_audioClient?.ConnectionState != ConnectionState.Connected)
        throw new InvalidOperationException("No voice connection established!");

    StopCurrentPlayback();
    _cts = new CancellationTokenSource();

    Log.Information("Playing: {Track}", track.Title);
    try
    {
        using var pipeYtToFfmpeg =
            new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var pipeYtClient =
            new AnonymousPipeClientStream(PipeDirection.In, pipeYtToFfmpeg.ClientSafePipeHandle);

        using var pipeFfmpegToDiscord =
            new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var pipeFfmpegOutput =
            new AnonymousPipeClientStream(PipeDirection.Out, pipeFfmpegToDiscord.ClientSafePipeHandle);

        // yt-dlp → pipe
        var ytDlpCmd = Cli.Wrap("yt-dlp")
            .WithArguments(["-f", "bestaudio", "-o", "-", track.Url])
            .WithStandardOutputPipe(PipeTarget.ToStream(pipeYtToFfmpeg))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => Log.Warning("[yt-dlp] {Line}", line)))
            .WithValidation(CommandResultValidation.None);
        
        // ffmpeg reads from yt-dlp and writes to pipe
        var ffmpegCmd = Cli.Wrap("ffmpeg")
            .WithArguments([
                "-hide_banner", "-loglevel", "error",
                "-re", "-i", "pipe:0",
                "-ac", "2", "-ar", "48000", "-f", "s16le",
                "-fflags", "+nobuffer", "-flags", "low_delay",
                "-avioflags", "direct", "-flush_packets", "1",
                "pipe:1"
            ])
            .WithStandardInputPipe(PipeSource.FromStream(pipeYtClient))
            .WithStandardOutputPipe(PipeTarget.ToStream(pipeFfmpegOutput))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => Log.Warning("[ffmpeg] {Line}", line)))
            .WithValidation(CommandResultValidation.None);
        

        // Send output from ffmpeg to Discord
        var sendTask = SendAudioAsync(pipeFfmpegToDiscord, _cts.Token);
        
        var ytDlpTask = ytDlpCmd.ExecuteAsync(_cts.Token);
        var ffmpegTask = ffmpegCmd.ExecuteAsync(_cts.Token);
        
        await Task.WhenAll(ytDlpTask, ffmpegTask, sendTask);
    }
    finally
    {
       await _cts.CancelAsync();
    }
}
    
    private void StartInactivityTimer()
    {
        _inactivityTimer = new Timer(60000);
        _inactivityTimer.Elapsed += CheckInactivity;
        _inactivityTimer.AutoReset = true;
        _inactivityTimer.Start();
    }

    private void CheckInactivity(object sender, ElapsedEventArgs e)
    {
        if ((DateTime.Now - _lastActivity).TotalMinutes < 1 || _audioClient == null) return;
        _ = Disconnect();
        Log.Information("Inactivity timed out");
    }
    
    private async Task SendAudioAsync(Stream sourceStream, CancellationToken cancellationToken)
    {
        const int blockSize = 3840; // 20 ms blocks for 48kHz
        var buffer = new byte[blockSize];
        int readBytes;

        try
        {
            if (_audioClient == null)
                throw new InvalidOperationException("No audio client available!");
            await using var discordStream = _audioClient.CreatePCMStream(AudioApplication.Mixed);
            while (!cancellationToken.IsCancellationRequested)
            {
                readBytes = await sourceStream.ReadAsync(buffer.AsMemory(0, blockSize), cancellationToken);
                // Log.Debug("Read {Bytes} bytes", readBytes);
                if (readBytes == 0)
                {
                    Log.Debug("EOF reached on sourceStream");
                    break;
                }
                // Log.Debug("Sending {Bytes} bytes", readBytes);
                await discordStream.WriteAsync(buffer.AsMemory(0,readBytes), cancellationToken);
                _lastActivity = DateTime.Now;
            }

            await discordStream.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Canceled on purpose
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Audio stream exception");
            throw;
        }
    }

    public static async Task<TrackInfo> GetTrackInfoAsync(string input)
    {
        var arguments = new[]
        {
            "-j",
            "--no-playlist",
            "--default-search", "ytsearch",
            input
        };

        // Capture stdout/stderr
        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();

        var result = await Cli.Wrap("yt-dlp")
            .WithArguments(arguments)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();

        // Check for errors
        if (result.ExitCode != 0)
        {
            Log.Error("yt-dlp error: {Error}", stdErrBuffer.ToString());
            throw new TrackNotFoundException();
        }

        // Parse JSON output
        using var doc = JsonDocument.Parse(stdOutBuffer.ToString());
        var root = doc.RootElement;

        return new TrackInfo
        {
            Title = root.GetProperty("title").GetString()!,
            Url = root.GetProperty("webpage_url").GetString()!,
            Thumbnail = root.GetProperty("thumbnail").GetString()!,
            Duration = TimeSpan.FromSeconds(root.GetProperty("duration").GetDouble()),
            Uploader = root.GetProperty("uploader").GetString()!,
            Query = input
        };
    }

    public void StopCurrentPlayback()
    {
        _cts?.Cancel();
    }

    public bool IsConnected =>
        _audioClient is { ConnectionState: ConnectionState.Connected }
        && !_cts.IsCancellationRequested;

    private static void CleanYtDlpFrags()
    {
        try
        {
            var frags = Directory.GetFiles(Directory.GetCurrentDirectory(), "--Frag*");
            foreach (var frag in frags)
            {
                File.Delete(frag);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error while cleaning up frags");
        }
    }

    public Task Disconnect()
    {
        StopCurrentPlayback();
        _audioClient?.StopAsync().Wait();
        _audioClient?.Dispose();
        CurrentChannel = null;
        _audioClient = null;
        CleanYtDlpFrags();
        
        return Task.CompletedTask;
    }

    void IDisposable.Dispose()
    {
        _cts?.Dispose();
        _inactivityTimer?.Dispose();
        _audioClient?.Dispose();
    }
    
}

public class TrackNotFoundException() : Exception("Track not found");