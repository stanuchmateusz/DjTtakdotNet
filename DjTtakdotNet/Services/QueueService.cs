using System.Collections;
using System.Collections.Concurrent;
using DjTtakdotNet.Music;

namespace DjTtakdotNet.Services;

public class QueueService
{
    public enum LoopMode { None, Single, All }
    
    private readonly ConcurrentQueue<TrackInfo> _queue = new();
    private TrackInfo _currentTrack;
    private LoopMode _loopMode = LoopMode.None;

    public void AddToQueue(TrackInfo track)
    {
        _queue.Enqueue(track);
    }

    public TrackInfo GetNextTrack()
    {
        
            if (_loopMode == LoopMode.Single && _currentTrack != null)
                return _currentTrack;

            if (_queue.TryDequeue(out var track))
            {
                if (_loopMode == LoopMode.All)
                    _queue.Enqueue(track);
                
                _currentTrack = track;
                return track;
            }

            if (_loopMode == LoopMode.Single && _currentTrack != null)
                return _currentTrack;

            _currentTrack = null;
            return null;
    }

    public void ClearQueue()
    {
        while (_queue.TryDequeue(out _)) { }
        _currentTrack = null;
    }
    
    public bool IsIdle()
    {
        return _queue.IsEmpty && _currentTrack == null;
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
        _loopMode = _loopMode switch
        {
            LoopMode.None => LoopMode.Single,
            LoopMode.Single => LoopMode.All,
            _ => LoopMode.None
        };
        return _loopMode;
    }

    public IEnumerable<TrackInfo> GetQueue() => _queue.ToArray();
    public TrackInfo CurrentTrack => _currentTrack;
    public LoopMode CurrentLoopMode => _loopMode;
}