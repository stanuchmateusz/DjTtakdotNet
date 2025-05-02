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
  
    public bool RemoveTrack(Guid trackId)
    {
        var tempList = _queue.ToList();
        var removed = tempList.RemoveAll(t => t.Id == trackId);
        _queue.Clear();
        foreach (var item in tempList)
            _queue.Enqueue(item);
        
        return removed > 0;
    }

    public LoopMode ToggleLoop()
    {
        CurrentLoopMode = CurrentLoopMode switch
        {
            LoopMode.None => LoopMode.Single,
            LoopMode.Single => LoopMode.All,
            _ => LoopMode.None
        };
        return CurrentLoopMode;
    }

    public IEnumerable<TrackInfo> GetQueue() => _queue.ToArray();
    public TrackInfo? CurrentTrack { get; private set; }
    public LoopMode CurrentLoopMode { get; private set; } = LoopMode.None;
}