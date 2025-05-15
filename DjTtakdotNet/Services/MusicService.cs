using Discord.Audio;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Timers;
using CliWrap;
using Discord;
using DjTtakdotNet.Music;
using DjTtakdotNet.Utils;
using Serilog;
using Timer = System.Timers.Timer;

namespace DjTtakdotNet.Services;

public class MusicService : IDisposable
{
    private bool _isProcessingQueue;
    private Timer _inactivityTimer;
    private DateTime _lastActivity;
    private IAudioClient? _audioClient;
    public IVoiceChannel? CurrentChannel { get; private set; }
    private CancellationTokenSource _playbackCts;
    private CancellationTokenSource _serviceProcessingCts;
    private readonly QueueService _queueService;
    private readonly IDjTtakConfig _config;
    private bool _advancingToNextGracefully = false;

    public MusicService(QueueService queueService, IDjTtakConfig config)
    {
        _queueService = queueService;
        _config = config;
        _playbackCts = new CancellationTokenSource();
        _serviceProcessingCts = new CancellationTokenSource();
    }

    public async Task JoinChannelAsync(IVoiceChannel channel)
    {
        if (_audioClient?.ConnectionState == ConnectionState.Connected && CurrentChannel?.Id == channel.Id)
        {
            Log.Debug("Already connected to {ChannelName}", channel.Name);
            return;
        }
        if (_audioClient != null && CurrentChannel?.Id != channel.Id)
        {
             Log.Information("DJ is on a different channel. Disconnecting from old one first.");
             await DisconnectAsyncInternal(); 
        }
        else if (_audioClient != null) 
        {
            await DisconnectAsyncInternal();
        }
        
        try
        {
            Log.Information("Joining channel {ChannelName}", channel.Name);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
            var connectTask = channel.ConnectAsync(selfDeaf: false);

            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            if (completedTask == timeoutTask)
            {
                await connectTask.ContinueWith(t => { if (t.IsFaulted) Log.Error(t.Exception, "Connection task faulted after timeout."); });
                throw new TimeoutException("Voice channel connection timed out!");
            }

            _audioClient = await connectTask;
            CurrentChannel = channel;
            _lastActivity = DateTime.Now;
            StartInactivityTimer();
            Log.Information("Successfully joined {ChannelName}", channel.Name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while joining channel {ChannelName}", channel.Name);
            _audioClient?.Dispose();
            _audioClient = null;
            CurrentChannel = null;
            throw new InvalidOperationException($"Error while joining channel: {ex.Message}");
        }
    }


    public bool StartQueue(TrackInfo track)
    {
        var wasEffectivelyIdle = _queueService.IsIdle() && _queueService.CurrentTrack == null;
        _queueService.AddToQueue(track);
        Log.Debug("Added {TrackTitle} to the queue", track.Title);

        if (_isProcessingQueue)
        {
            return wasEffectivelyIdle;
        }
        
        _serviceProcessingCts = new CancellationTokenSource();
        _ = ProcessQueueAsync(_serviceProcessingCts.Token);
        return true;
    }


    private async Task ProcessQueueAsync(CancellationToken serviceToken)
    {
        if (_isProcessingQueue)
        {
            Log.Warning("ProcessQueueAsync called while already processing.");
            return;
        }
        _isProcessingQueue = true;
        Log.Information("Music queue processing started.");

        try
        {
            while (!serviceToken.IsCancellationRequested)
            {
                var track = _queueService.SetNextTrackAsCurrent();
                if (track == null)
                {
                    Log.Debug("Queue is empty or not looping single, pausing processing until new tracks are added.");
                    
                    if (_queueService.IsIdle()) break;
                    await Task.Delay(100, serviceToken); 
                    continue;
                }

                Log.Debug("Processing track {TrackTitle} from queue", track.Title);
                try
                {
                    if (CurrentChannel == null && _audioClient != null) 
                    {
                        Log.Warning("CurrentChannel is null but AudioClient exists. Attempting to disconnect client.");
                        await _audioClient.StopAsync();
                        _audioClient.Dispose();
                        _audioClient = null;
                    }
                    
                    if (_audioClient == null || _audioClient.ConnectionState != ConnectionState.Connected)
                    {
                        Log.Information("No voice connection or not connected. Attempting to join channel for track: {TrackTitle}", track.Title);
                        if (CurrentChannel == null)
                        {
                            Log.Error("Cannot play track {TrackTitle}: CurrentChannel is not set. Skipping track.", track.Title);
                            _queueService.SkipCurrentTrack(); 
                            continue;
                        }
                        await JoinChannelAsync(CurrentChannel); 
                    }
                    
                    _playbackCts = new CancellationTokenSource(); 
                    await PlayTrackAsync(track, _playbackCts.Token);
                    Log.Information("Track finished");
                    _queueService.TrackFinishedProcessing(); 
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("No voice connection") || ex.Message.Contains("Not connected"))
                {
                    Log.Warning(ex, "No voice connection while trying to play {TrackTitle}. Will attempt to rejoin or skip.", track.Title);
                    if (serviceToken.IsCancellationRequested) break;

                    if (CurrentChannel != null)
                    {
                        Log.Information("Attempting to rejoin channel {ChannelName} for {TrackTitle}", CurrentChannel.Name, track.Title);
                        try
                        {
                            await JoinChannelAsync(CurrentChannel);
                            // If JoinChannelAsync succeeds, the loop will re-attempt to play `track` in the next iteration
                            // because `SetNextTrackAsCurrent` will return it again (as it wasn't processed).
                            // No need to call PlayTrackAsync here directly.
                            Log.Information("Rejoined channel, will retry track {TrackTitle}", track.Title);
                            continue; 
                        }
                        catch (Exception rejoinEx)
                        {
                            Log.Error(rejoinEx, "Failed to rejoin channel for track {TrackTitle}. Skipping track.", track.Title);
                            _queueService.SkipCurrentTrack(); // Give up on this track
                            continue;
                        }
                    }
                    Log.Error("Cannot play track {TrackTitle}: CurrentChannel is not set after connection loss. Skipping track.", track.Title);
                    _queueService.SkipCurrentTrack(); 
                }
                catch (OperationCanceledException) // _playbackCts cancellation
                {
                    if (serviceToken.IsCancellationRequested)
                    {
                        Log.Information("Service token cancelled, playback of {TrackTitle} also cancelled.", track?.Title ?? "Unknown Track");
                        break; 
                    }

                    if (track != null)
                    {
                        Log.Information("Playback of {TrackTitle} was cancelled by user request.", track.Title);
                        if (_advancingToNextGracefully)
                        {
                            Log.Information("Processing as 'next' command: {TrackTitle} will be treated as finished.", track.Title);
                            _queueService.TrackFinishedProcessing(); 
                        }
                        else
                        {
                            Log.Information("Processing as 'skip' command: {TrackTitle} will be skipped.", track.Title);
                            _queueService.SkipCurrentTrack();
                        }
                    }
                    else
                    {
                        Log.Warning("Playback cancelled, but current track was null. Advancing queue pointer if possible (defaulting to skip).");
                        _queueService.SkipCurrentTrack(); 
                    }
                    _advancingToNextGracefully = false; 
                }
                catch (TrackNotFoundException ex)
                {
                     Log.Error(ex, "TrackNotFoundException for {TrackTitle} during PlayTrackAsync. Skipping.", track.Title);
                    _queueService.SkipCurrentTrack(); 
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unknown error while playing track {TrackTitle}. Skipping track.", track.Title);
                    _queueService.SkipCurrentTrack();
                }
            }
        }
        catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
        {
            Log.Information("Music queue processing was cancelled by service token.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Critical error in ProcessQueueAsync loop.");
        }
        finally
        {
            Log.Information("Music queue processing stopped.");
            _isProcessingQueue = false;
            // Do NOT clear the queue here. It should persist.
        }
    }

    private async Task PlayTrackAsync(TrackInfo track, CancellationToken playbackToken)
    {
        if (_audioClient?.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("No voice connection established!");
        
        Log.Information("Playing: {TrackTitle}", track.Title);
        try
        {
            await using var pipeYtToFfmpeg =
                new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            await using var pipeYtClient =
                new AnonymousPipeClientStream(PipeDirection.In, pipeYtToFfmpeg.ClientSafePipeHandle);

            await using var pipeFfmpegToDiscord =
                new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            await using var pipeFfmpegOutput =
                new AnonymousPipeClientStream(PipeDirection.Out, pipeFfmpegToDiscord.ClientSafePipeHandle);

            var ytDlpCmd = Cli.Wrap("yt-dlp")
                .WithArguments(["-f", "bestaudio", "-o", "-", track.Url])
                .WithStandardOutputPipe(PipeTarget.ToStream(pipeYtToFfmpeg))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(line => Log.Debug("[yt-dlp] {Line}", line)))
                .WithValidation(CommandResultValidation.None);

            var ffmpegCmd = Cli.Wrap("ffmpeg")
                .WithArguments([
                    "-hide_banner", "-loglevel", "error",
                    "-re", "-i", "pipe:0", // Read from stdin (pipe from yt-dlp)
                    "-ac", "2", "-ar", "48000", "-f", "s16le",
                    "-fflags", "+nobuffer", "-flags", "low_delay",
                    "-avioflags", "direct", "-flush_packets", "1",
                    "pipe:1" // Output to stdout (pipe to Discord)
                ])
                .WithStandardInputPipe(PipeSource.FromStream(pipeYtClient))
                .WithStandardOutputPipe(PipeTarget.ToStream(pipeFfmpegOutput))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(line => Log.Debug("[ffmpeg] {Line}", line)))
                .WithValidation(CommandResultValidation.None);

            var ytDlpTask = ytDlpCmd.ExecuteAsync(playbackToken);
            SendAudioAsync(pipeFfmpegToDiscord, playbackToken); //DO NOT await - it would deadlock the processes 
            ffmpegCmd.ExecuteAsync(playbackToken);

            await ytDlpTask;
            
            Log.Debug("Finished playing {TrackTitle}", track.Title);
        }
        catch (OperationCanceledException) when (playbackToken.IsCancellationRequested)
        {
            Log.Information("Playback of {TrackTitle} cancelled via token.", track.Title);
            throw; // Re-throw for ProcessQueueAsync to handle
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during PlayTrackAsync for {TrackTitle}", track.Title);
            throw; // Re-throw for ProcessQueueAsync to handle
        }
    }

    private void StartInactivityTimer()
    {
        _inactivityTimer?.Dispose(); 
        _inactivityTimer = new Timer(_config.InactivityTimeoutMilliseconds);
        _inactivityTimer.Elapsed += CheckInactivity;
        _inactivityTimer.AutoReset = true;
        _inactivityTimer.Start();
        Log.Debug("Inactivity timer started.");
    }

    private void StopInactivityTimer()
    {
        _inactivityTimer?.Stop();
        _inactivityTimer?.Dispose();
        _inactivityTimer = null;
        Log.Debug("Inactivity timer stopped.");
    }


    private void CheckInactivity(object? sender, ElapsedEventArgs e)
    {
        if (_isProcessingQueue && _queueService.CurrentTrack != null)
        {
            _lastActivity = DateTime.Now;
            return;
        }

        if (!((DateTime.Now - _lastActivity).TotalMinutes >= 1) || _audioClient == null) return;
        Log.Information("Inactivity timeout reached. Disconnecting.");
        _ = DisconnectAsync();
    }

    private async Task SendAudioAsync(Stream sourceStream, CancellationToken cancellationToken)
    {
        const int blockSize = 3840; 
        var buffer = new byte[blockSize];
        int readBytes;

        try
        {
            if (_audioClient == null)
                throw new InvalidOperationException("No audio client available for sending audio!");
            if (_audioClient.ConnectionState != ConnectionState.Connected)
                 throw new InvalidOperationException("Audio client not connected for sending audio!");

            await using var discordStream = _audioClient.CreatePCMStream(AudioApplication.Mixed);
            while (!cancellationToken.IsCancellationRequested)
            {
                readBytes = await sourceStream.ReadAsync(buffer.AsMemory(0, blockSize), cancellationToken);
                if (readBytes == 0)
                {
                    Log.Debug("EOF reached on sourceStream for SendAudioAsync.");
                    break;
                }
                await discordStream.WriteAsync(buffer.AsMemory(0, readBytes), cancellationToken);
                _lastActivity = DateTime.Now;
            }
            await discordStream.FlushAsync(cancellationToken);
            Log.Debug("SendAudioAsync finished flushing stream or was cancelled.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("SendAudioAsync was cancelled.");
            throw; 
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception in SendAudioAsync.");
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
        
        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();

        var result = await Cli.Wrap("yt-dlp")
            .WithArguments(arguments)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
            .WithValidation(CommandResultValidation.None) 
            .ExecuteAsync();
        
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(stdOutBuffer.ToString()))
        {
            var errorMessage = stdErrBuffer.ToString();
            Log.Error("yt-dlp error (Exit Code: {ExitCode}): {Error}. StdOut: {StdOut}", result.ExitCode, errorMessage, stdOutBuffer.ToString());
            throw new TrackNotFoundException($"Failed to get track info. yt-dlp: {errorMessage}");
        }
        
        try
        {
            using var doc = JsonDocument.Parse(stdOutBuffer.ToString());
            var root = doc.RootElement;

            return new TrackInfo
            {
                Title = root.GetProperty("title").GetString()!,
                Url = root.GetProperty("webpage_url").GetString()!,
                Thumbnail = root.TryGetProperty("thumbnail", out var thumb) ? thumb.GetString()! : "https://via.placeholder.com/150",
                Duration = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(dur.GetDouble()) : TimeSpan.Zero,
                Uploader = root.TryGetProperty("uploader", out var upl) ? upl.GetString()! : "Unknown",
                Query = input
            };
        }
        catch (JsonException jex)
        {
            Log.Error(jex, "Failed to parse yt-dlp JSON output: {JsonOutput}", stdOutBuffer.ToString());
            throw new TrackNotFoundException("Could not parse track information.");
        }
        catch (KeyNotFoundException knfex)
        {
            Log.Error(knfex, "Missing expected property in yt-dlp JSON output: {JsonOutput}", stdOutBuffer.ToString());
            throw new TrackNotFoundException("Track information is incomplete.");
        }
    }

    public void StopCurrentPlayback(bool advanceGracefully = false)
    {
        Log.Debug("StopCurrentPlayback called. AdvanceGracefully: {AdvanceGracefully}", advanceGracefully);
        _advancingToNextGracefully = advanceGracefully;
        _playbackCts?.Cancel();
    }

    public bool IsConnected =>
        _audioClient is { ConnectionState: ConnectionState.Connected };

    private static void CleanYtDlpFrags()
    {
        try
        {
            var frags = Directory.GetFiles(Directory.GetCurrentDirectory(), "*--Frag*");
            foreach (var frag in frags)
            {
                Log.Debug("Deleting fragment file: {FragmentFile}", frag);
                File.Delete(frag);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error while cleaning up yt-dlp fragments");
        }
    }
    
    private async Task DisconnectAsyncInternal(bool stopProcessing = false)
    {
        Log.Information("Internal disconnect called. StopProcessing: {StopProcessing}", stopProcessing);
        StopCurrentPlayback();

        if (stopProcessing)
        {
            await _serviceProcessingCts.CancelAsync(); 
        }

        if (_audioClient != null)
        {
            try
            {
                await _audioClient.StopAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Exception during AudioClient.StopAsync()");
            }
            _audioClient.Dispose();
            _audioClient = null;
        }
        CurrentChannel = null;
        CleanYtDlpFrags();
        StopInactivityTimer();
        Log.Information("Internal disconnect finished.");
    }
    
    public async Task DisconnectAsync()
    {
        Log.Information("DisconnectAsync called. Stopping queue processing and disconnecting from voice.");
        await DisconnectAsyncInternal(stopProcessing: true);
        _queueService.ClearQueue();
    }

    void IDisposable.Dispose()
    {
        Log.Debug("MusicService Dispose executing.");
        _serviceProcessingCts?.Cancel();
        _serviceProcessingCts?.Dispose();
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();
        _inactivityTimer?.Dispose();
        _audioClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class TrackNotFoundException : Exception
{
    public TrackNotFoundException() : base("Track not found") { }
    public TrackNotFoundException(string message) : base(message) { }
    public TrackNotFoundException(string message, Exception inner) : base(message, inner) { }
}