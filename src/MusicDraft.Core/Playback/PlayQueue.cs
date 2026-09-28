namespace MusicDraft.Core.Playback;

public enum RepeatMode { Off, All, One }

/// <summary>
/// Deterministic play-order state machine over track IDs. Knows nothing about audio devices.
/// Shuffle uses a non-repeating cycle over playable tracks; Previous walks back through actual play history.
/// </summary>
public sealed class PlayQueue
{
    private readonly List<long> _items = [];
    private readonly Func<long, bool> _isPlayable;
    private readonly Random _rng;
    private readonly List<int> _history = [];   // indices actually played, in order
    private int _historyCursor = -1;             // position in _history of the current item
    private List<int> _shuffleRemaining = [];    // indices not yet played in the current shuffle cycle
    private bool _shuffle;

    public PlayQueue(Func<long, bool> isPlayable, Random? rng = null)
    {
        _isPlayable = isPlayable;
        _rng = rng ?? new Random();
    }

    public IReadOnlyList<long> Items => _items;
    public int CurrentIndex { get; private set; } = -1;
    public long? Current => CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex] : null;
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            ResetShuffleCycle();
        }
    }

    public event Action? Changed;

    public void Replace(IEnumerable<long> trackIds, int startIndex = -1)
    {
        _items.Clear();
        _items.AddRange(trackIds);
        _history.Clear();
        _historyCursor = -1;
        CurrentIndex = -1;
        ResetShuffleCycle();
        if (startIndex >= 0 && startIndex < _items.Count) JumpTo(startIndex);
        Changed?.Invoke();
    }

    public void Append(IEnumerable<long> trackIds)
    {
        var start = _items.Count;
        _items.AddRange(trackIds);
        for (var i = start; i < _items.Count; i++) _shuffleRemaining.Add(i);
        Changed?.Invoke();
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items.RemoveAt(index);
        static int Shift(int i, int removed) => i > removed ? i - 1 : i;
        // Drop the removed index from history and remap the rest.
        var newHistory = new List<int>();
        var newCursor = -1;
        for (var h = 0; h < _history.Count; h++)
        {
            if (_history[h] == index) { if (h <= _historyCursor) newCursor = newHistory.Count - 1; continue; }
            if (h == _historyCursor) newCursor = newHistory.Count;
            newHistory.Add(Shift(_history[h], index));
        }
        _history.Clear();
        _history.AddRange(newHistory);
        _historyCursor = Math.Min(newCursor, _history.Count - 1);
        _shuffleRemaining = _shuffleRemaining.Where(i => i != index).Select(i => Shift(i, index)).ToList();
        if (CurrentIndex == index) CurrentIndex = -1; // current item removed: caller decides whether to advance
        else CurrentIndex = Shift(CurrentIndex, index);
        Changed?.Invoke();
    }

    public void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _items.Count || to >= _items.Count) return;
        var id = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, id);
        int Map(int i) => i == from ? to : (from < to ? (i > from && i <= to ? i - 1 : i) : (i >= to && i < from ? i + 1 : i));
        for (var h = 0; h < _history.Count; h++) _history[h] = Map(_history[h]);
        _shuffleRemaining = _shuffleRemaining.Select(Map).ToList();
        if (CurrentIndex >= 0) CurrentIndex = Map(CurrentIndex);
        Changed?.Invoke();
    }

    /// <summary>Start playing a specific index (user click). Returns false if not playable.</summary>
    public bool JumpTo(int index)
    {
        if (index < 0 || index >= _items.Count || !_isPlayable(_items[index])) return false;
        SetCurrent(index, recordHistory: true);
        return true;
    }

    /// <summary>User pressed Next. Repeat-one does not trap the user on one track.</summary>
    public long? Next() => Advance(userInitiated: true);

    /// <summary>Current track finished naturally.</summary>
    public long? OnTrackEnded()
    {
        if (Repeat == RepeatMode.One && Current is { } c && _isPlayable(c)) return c;
        return Advance(userInitiated: false);
    }

    public long? Previous()
    {
        // Walk back through actual history first (shuffle-aware).
        while (_historyCursor > 0)
        {
            _historyCursor--;
            var idx = _history[_historyCursor];
            if (idx < _items.Count && _isPlayable(_items[idx]))
            {
                CurrentIndex = idx;
                Changed?.Invoke();
                return _items[idx];
            }
        }
        if (Shuffle) return Current;
        // Without history (e.g. after a jump), step back in list order.
        for (var i = CurrentIndex - 1; i >= 0; i--)
        {
            if (_isPlayable(_items[i])) { SetCurrent(i, recordHistory: true); return _items[i]; }
        }
        if (Repeat == RepeatMode.All)
        {
            for (var i = _items.Count - 1; i > CurrentIndex; i--)
                if (_isPlayable(_items[i])) { SetCurrent(i, recordHistory: true); return _items[i]; }
        }
        return Current;
    }

    private long? Advance(bool userInitiated)
    {
        // Re-walk forward history if the user went back.
        if (_historyCursor >= 0 && _historyCursor < _history.Count - 1)
        {
            _historyCursor++;
            var idx = _history[_historyCursor];
            if (idx < _items.Count && _isPlayable(_items[idx]))
            {
                CurrentIndex = idx;
                Changed?.Invoke();
                return _items[idx];
            }
            _history.RemoveRange(_historyCursor, _history.Count - _historyCursor);
            _historyCursor = _history.Count - 1;
        }

        var next = Shuffle ? NextShuffled() : NextSequential();
        if (next < 0)
        {
            // End of queue with repeat off. A natural end stops playback; a Next click at the end is a no-op.
            if (!userInitiated) { CurrentIndex = -1; Changed?.Invoke(); }
            return null;
        }
        SetCurrent(next, recordHistory: true);
        return _items[next];
    }

    private int NextSequential()
    {
        for (var i = CurrentIndex + 1; i < _items.Count; i++)
            if (_isPlayable(_items[i])) return i;
        if (Repeat != RepeatMode.Off)
            for (var i = 0; i <= Math.Min(CurrentIndex, _items.Count - 1); i++)
                if (_isPlayable(_items[i])) return i;
        return -1;
    }

    private int NextShuffled()
    {
        _shuffleRemaining.RemoveAll(i => i == CurrentIndex || i >= _items.Count || !_isPlayable(_items[i]));
        if (_shuffleRemaining.Count == 0)
        {
            if (Repeat == RepeatMode.Off) return -1;
            ResetShuffleCycle();
            _shuffleRemaining.RemoveAll(i => !_isPlayable(_items[i]));
            // Avoid immediately repeating the track that just played when there's a choice.
            if (_shuffleRemaining.Count > 1) _shuffleRemaining.Remove(CurrentIndex);
            if (_shuffleRemaining.Count == 0) return -1;
        }
        var pick = _shuffleRemaining[_rng.Next(_shuffleRemaining.Count)];
        _shuffleRemaining.Remove(pick);
        return pick;
    }

    private void ResetShuffleCycle()
    {
        _shuffleRemaining = Enumerable.Range(0, _items.Count).Where(i => i != CurrentIndex).ToList();
    }

    private void SetCurrent(int index, bool recordHistory)
    {
        CurrentIndex = index;
        _shuffleRemaining.Remove(index);
        if (recordHistory)
        {
            if (_historyCursor < _history.Count - 1) _history.RemoveRange(_historyCursor + 1, _history.Count - _historyCursor - 1);
            _history.Add(index);
            _historyCursor = _history.Count - 1;
        }
        Changed?.Invoke();
    }
}
