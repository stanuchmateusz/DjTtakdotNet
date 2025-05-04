using System.Collections;
using System.Collections.Concurrent;
using DjTtakdotNet.Music;

namespace DjTtakdotNet.Services;

public class QueueService
{
    public enum LoopMode { None, Single, All }
    
    private readonly ConcurrentQueue<TrackInfo> _queue = new();

    public void AddToQueue(TrackInfo track)
    {
        _queue.Enqueue(track);
    }

    public TrackInfo? GetNextTrack()
    {
        
            if (CurrentLoopMode == LoopMode.Single && CurrentTrack != null)
                return CurrentTrack;

            if (_queue.TryDequeue(out var track))
            {
                if (CurrentLoopMode == LoopMode.All)
                    _queue.Enqueue(track);
                
                CurrentTrack = track;
                return track;
            }

            if (CurrentLoopMode == LoopMode.Single && CurrentTrack != null)
                return CurrentTrack;

            CurrentTrack = null;
            return null;
    }

    public void ClearQueue()
    {
        while (_queue.TryDequeue(out _)) { }
        CurrentTrack = null;
    }
    
    public bool IsIdle()
    {
        return _queue.IsEmpty && CurrentTrack == null;
    }
  
    public bool RemoveTrack(int trackPos)
    {
        if (trackPos < 0 || trackPos >= _queue.Count)
            return false;

        var tempList = _queue.ToList();
        tempList.RemoveAt(trackPos);
    
        _queue.Clear();
        foreach (var item in tempList)
            _queue.Enqueue(item);
    
        return true;
    }
        
    public IEnumerable<TrackInfo> GetQueue() => _queue.ToArray();
    public TrackInfo? CurrentTrack { get; private set; }
    public LoopMode CurrentLoopMode { get; set; } = LoopMode.None;
}