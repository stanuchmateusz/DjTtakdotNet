using System.Collections.Concurrent;
using DjTtakdotNet.Music;
using Serilog; // Added for logging potential inconsistencies

namespace DjTtakdotNet.Services;

public class QueueService
{
    public enum LoopMode { None, Single, All }

    private readonly ConcurrentQueue<TrackInfo> _queue = new();

    public TrackInfo? CurrentTrack { get; private set; }
    public LoopMode CurrentLoopMode { get; set; } = LoopMode.None;

    public void AddToQueue(TrackInfo track)
    {
        _queue.Enqueue(track);
    }

    /// <summary>
    /// Sets the next track from the queue as CurrentTrack without removing it from the queue.
    /// Handles LoopMode.Single by returning the existing CurrentTrack if applicable.
    /// </summary>
    /// <returns>The track to be played, or null if the queue is empty and not looping single.</returns>
    public TrackInfo? SetNextTrackAsCurrent()
    {

        if (CurrentLoopMode == LoopMode.Single && CurrentTrack != null)
        {
            return CurrentTrack;
        }

        if (_queue.TryPeek(out var track))
        {
            CurrentTrack = track;
            return track;
        }

        CurrentTrack = null;
        return null;
    }

    /// <summary>
    /// Called when the CurrentTrack has finished processing (played successfully or failed terminally).
    /// Removes the CurrentTrack from the queue and handles LoopMode.All.
    /// For LoopMode.Single, CurrentTrack is preserved for the next SetNextTrackAsCurrent call.
    /// </summary>
    public void TrackFinishedProcessing()
    {
        if (CurrentTrack == null) 
        {
            return;
        }
        
        if (CurrentLoopMode == LoopMode.Single)
        {
            return;
        }

        if (_queue.TryPeek(out var headOfQueue) && headOfQueue == CurrentTrack)
        {
            _queue.TryDequeue(out var playedTrack);

            if (playedTrack != null && CurrentLoopMode == LoopMode.All)
            {
                _queue.Enqueue(playedTrack); 
            }
        }
        else if (CurrentTrack != null)
        {
             Log.Warning("TrackFinishedProcessing: CurrentTrack '{CurrentTrackTitle}' was not at the head of the queue or queue was empty. It might have been removed externally.", CurrentTrack.Title);
        }
        
        CurrentTrack = null;
    }

    /// <summary>
    /// Called when the CurrentTrack is skipped.
    /// Ensures the track is removed from immediate playback and handles LoopMode.All.
    /// Unlike TrackFinishedProcessing, this will advance past a LoopMode.Single track.
    /// </summary>
    public void SkipCurrentTrack()
    {
        if (CurrentTrack == null)
        {
            return;
        }

        TrackInfo trackThatWasSkipped = CurrentTrack; 

        if (_queue.TryPeek(out var headOfQueue) && headOfQueue == trackThatWasSkipped)
        {
            _queue.TryDequeue(out _); 
        }
        
        if (CurrentLoopMode == LoopMode.All)
        {
            _queue.Enqueue(trackThatWasSkipped);
        }

        CurrentTrack = null;
    }


    public void ClearQueue()
    {
        while (_queue.TryDequeue(out _)) { }
        CurrentTrack = null;
    }

    public bool IsIdle()
    {
        if (CurrentLoopMode == LoopMode.Single && CurrentTrack != null)
        {
            return false;
        }
        return _queue.IsEmpty;
    }

    public bool RemoveTrack(int trackPos)
    {
        if (trackPos < 0 || trackPos >= _queue.Count)
            return false;

        var tempList = _queue.ToList();
        var removedTrack = tempList[trackPos];
        tempList.RemoveAt(trackPos);

        _queue.Clear(); // Clear and re-populate
        foreach (var item in tempList)
            _queue.Enqueue(item);
        
        if (CurrentTrack == removedTrack)
        {
           Log.Debug("Removed track '{TrackTitle}' was the CurrentTrack.", removedTrack.Title);
        }

        return true;
    }

    public IEnumerable<TrackInfo> GetQueue() => _queue.ToArray();
}