using Discord.Audio;
using Discord.WebSocket;
using System.Diagnostics;
using System.Text.Json;
using System.Timers;
using Discord;
using DjTtakdotNet.Music;
using Serilog;
using Timer = System.Timers.Timer;

namespace DjTtakdotNet.Services;

public class MusicService : IDisposable
{
    private IAudioClient _audioClient;
    private Process _audioProcess;
    private CancellationTokenSource _cts;
    private Timer _inactivityTimer;
    private DateTime _lastActivity;
    public IVoiceChannel currentChannel { get; }
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
            throw new InvalidOperationException("Bot jest już podłączony do innego kanału!");

        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
            var connectTask = channel.ConnectAsync(selfDeaf: false);

            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            if (completedTask == timeoutTask)
                throw new TimeoutException("Przekroczono czas łączenia z kanałem głosowym");

            _audioClient = await connectTask;
            _lastActivity = DateTime.Now;
            StartInactivityTimer();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Błąd łączenia z kanałem");
            throw new InvalidOperationException("Nie udało się połączyć z kanałem głosowym");
        }
    }


    public async Task<bool> PlayAsync(TrackInfo track)
    {
        bool wasIdle = _queueService.IsIdle();
        _queueService.AddToQueue(track);

        if (wasIdle)
        {
                if (!_isProcessingQueue)
                {
                    _isProcessingQueue = true;
                    _ = ProcessQueueAsync();
                }
                
            return true;
        }

        return false;
    }


    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                var track = _queueService.GetNextTrack();
                if (track == null)
                {
                    // Sprawdź ponownie po krótkim opóźnieniu, aby uniknąć ciągłego sprawdzania
                    await Task.Delay(1000);
                    if (_queueService.IsIdle()) break;
                    continue;
                }

                await PlayTrackAsync(track);
            }
        }
        finally
        {
            _isProcessingQueue = false;
        }
    }


    public async Task PlayTrackAsync(TrackInfo track)
    {
        try
        {
            if (_audioClient?.ConnectionState != ConnectionState.Connected)
                throw new InvalidOperationException("Brak połączenia głosowego");

            StopCurrentPlayback();
            _cts = new CancellationTokenSource();


            Log.Information("Odtwarzanie: {Track}", track.Title);

            var arguments = BuildProcessArguments(track.Url);

            var startInfo = new ProcessStartInfo
            {
                FileName = GetShellCommand(),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _audioProcess = new Process { StartInfo = startInfo };
            _audioProcess.EnableRaisingEvents = true;
            _audioProcess.Exited += (sender, args) =>
            {
                Log.Information("Proces audio zakończony: {ExitCode}", _audioProcess.ExitCode);
            };

            if (!_audioProcess.Start())
                throw new InvalidOperationException("Nie udało się uruchomić procesu audio");

            _ = LogProcessErrors(_audioProcess.StandardError);

            await SendAudioAsync(_audioProcess.StandardOutput.BaseStream, _cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Błąd odtwarzania");
            throw;
        }
    }

    private static async Task LogProcessErrors(StreamReader errorStream)
    {
        try
        {
            string error;
            while ((error = await errorStream.ReadLineAsync()) != null)
            {
                if (!string.IsNullOrWhiteSpace(error))
                    Log.Information("Info procesu: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Błąd czytania strumienia błędów");
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
        _ = DisconnectAsync();
        Log.Information("Inactivity timed out");
    }

    private static string BuildProcessArguments(string input)
    {
        var ytdlpCmd = $"yt-dlp -f bestaudio -o - \"{input}\"";
        var ffmpegCmd = $"ffmpeg -hide_banner -loglevel error -re -i pipe:0 " +
                        $"-ac 2 -ar 48000 -f s16le -fflags +nobuffer -flags low_delay -avioflags direct -flush_packets 1 pipe:1\"";

        return $"/C \"{ytdlpCmd} | {ffmpegCmd}\"";
    }

    private static string GetShellCommand() =>
        Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd.exe" : "/bin/bash";

    private async Task SendAudioAsync(Stream sourceStream, CancellationToken cancellationToken)
    {
        const int blockSize = 3840; // 20 ms blocks for 48kHz
        var buffer = new byte[blockSize];
        int readBytes;

        try
        {
            await using var discordStream = _audioClient.CreatePCMStream(AudioApplication.Mixed);
            while (!cancellationToken.IsCancellationRequested)
            {
                readBytes = await sourceStream.ReadAsync(buffer, 0, blockSize, cancellationToken);

                if (readBytes == 0)
                    break;

                await discordStream.WriteAsync(buffer, 0, readBytes, cancellationToken);
                _lastActivity = DateTime.Now; // Update the timer
            }

            await discordStream.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Canceled on purpose
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Błąd strumienia audio");
            throw;
        }
    }

    public static async Task<TrackInfo> GetTrackInfoAsync(string input)
    {
        var arguments = $"-j --no-playlist --default-search \"ytsearch\" \"{input}\"";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var jsonOutput = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new TrackNotFoundException();

        using var doc = JsonDocument.Parse(jsonOutput);
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
        _audioProcess?.Kill();
    }

    public bool IsConnected
    {
        get
        {
            return _audioClient != null
                   && _audioClient.ConnectionState == ConnectionState.Connected
                   && !_cts.IsCancellationRequested;
        }
    }

    public async Task DisconnectAsync()
    {
        StopCurrentPlayback();
        _audioClient?.StopAsync().Wait();
        _audioClient?.Dispose();
        _audioClient = null;
    }

    void IDisposable.Dispose()
    {
        _cts?.Dispose();
        _inactivityTimer?.Dispose();
        _audioClient?.Dispose();
        _audioProcess.Dispose();
    }
    
}

public class TrackNotFoundException() : Exception("Track not found");