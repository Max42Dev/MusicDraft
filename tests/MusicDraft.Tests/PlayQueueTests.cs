using MusicDraft.Core.Playback;

namespace MusicDraft.Tests;

public class PlayQueueTests
{
    private static PlayQueue Make(IEnumerable<long> ids, Func<long, bool>? playable = null, int seed = 1)
    {
        var q = new PlayQueue(playable ?? (_ => true), new Random(seed));
        q.Replace(ids, 0);
        return q;
    }

    [Fact]
    public void Empty_queue_is_deterministic()
    {
        var q = new PlayQueue(_ => true);
        q.Replace([]);
        Assert.Null(q.Current);
        Assert.Null(q.Next());
        Assert.Null(q.Previous());
        Assert.Null(q.OnTrackEnded());
        Assert.False(q.JumpTo(0));
    }

    [Fact]
    public void Repeat_off_stops_at_end_and_next_is_noop()
    {
        var q = Make([1, 2, 3]);
        Assert.Equal(2, q.Next());
        Assert.Equal(3, q.Next());
        Assert.Null(q.Next());
        Assert.Equal(3, q.Current); // user Next at end keeps current
        Assert.Null(q.OnTrackEnded());
        Assert.Null(q.Current);     // natural end stops
    }

    [Fact]
    public void Repeat_all_wraps()
    {
        var q = Make([1, 2]);
        q.Repeat = RepeatMode.All;
        Assert.Equal(2, q.OnTrackEnded());
        Assert.Equal(1, q.OnTrackEnded());
    }

    [Fact]
    public void Repeat_one_repeats_on_end_but_next_advances()
    {
        var q = Make([1, 2]);
        q.Repeat = RepeatMode.One;
        Assert.Equal(1, q.OnTrackEnded());
        Assert.Equal(2, q.Next());
    }

    [Fact]
    public void Skips_unplayable_tracks()
    {
        var missing = new HashSet<long> { 2, 3 };
        var q = Make([1, 2, 3, 4], id => !missing.Contains(id));
        Assert.Equal(4, q.Next());
        Assert.Equal(1, q.Previous());
        Assert.False(q.JumpTo(1));
    }

    [Fact]
    public void All_unplayable_with_repeat_all_does_not_loop_forever()
    {
        var q = new PlayQueue(_ => false);
        q.Replace([1, 2, 3], 0);
        q.Repeat = RepeatMode.All;
        Assert.Null(q.Current);
        Assert.Null(q.Next());
        Assert.Null(q.OnTrackEnded());
    }

    [Fact]
    public void Shuffle_is_a_non_repeating_cycle()
    {
        var ids = Enumerable.Range(1, 10).Select(i => (long)i).ToList();
        var q = Make(ids, seed: 42);
        q.Shuffle = true;
        var seen = new List<long> { q.Current!.Value };
        for (var i = 0; i < 9; i++) seen.Add(q.Next()!.Value);
        Assert.Equal(10, seen.Distinct().Count());
        Assert.Null(q.Next()); // cycle exhausted, repeat off
    }

    [Fact]
    public void Shuffle_previous_walks_history_then_next_replays_it()
    {
        var q = Make(Enumerable.Range(1, 8).Select(i => (long)i), seed: 7);
        q.Shuffle = true;
        var a = q.Current;
        var b = q.Next();
        var c = q.Next();
        Assert.Equal(b, q.Previous());
        Assert.Equal(a, q.Previous());
        Assert.Equal(b, q.Next());
        Assert.Equal(c, q.Next());
    }

    [Fact]
    public void Shuffle_repeat_all_starts_new_cycle_without_immediate_repeat()
    {
        var q = Make([1, 2, 3], seed: 3);
        q.Shuffle = true;
        q.Repeat = RepeatMode.All;
        long? last = q.Current;
        for (var i = 0; i < 30; i++)
        {
            var n = q.OnTrackEnded();
            Assert.NotNull(n);
            Assert.NotEqual(last, n);
            last = n;
        }
    }

    [Fact]
    public void Removing_current_clears_it_and_next_continues_from_list()
    {
        var q = Make([1, 2, 3]);
        q.Next(); // 2
        q.RemoveAt(1);
        Assert.Null(q.Current);
        Assert.Equal([1L, 3L], q.Items);
        Assert.NotNull(q.Next());
    }

    [Fact]
    public void Move_keeps_current_track()
    {
        var q = Make([1, 2, 3, 4]);
        q.Next(); // 2 at index 1
        q.Move(1, 3);
        Assert.Equal(2, q.Current);
        Assert.Equal(3, q.CurrentIndex);
        q.Move(0, 2);
        Assert.Equal(2, q.Current);
        Assert.Equal(new long[] { 3, 4, 1, 2 }, q.Items);
    }

    [Fact]
    public void Previous_from_first_without_repeat_stays()
    {
        var q = Make([1, 2]);
        Assert.Equal(1, q.Previous());
    }
}
