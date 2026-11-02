using AlbionLocal.Photon;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// When and how the live characters are saved (TC: the character DB worker; ARCHITECTURE.md §4.4, §5.3). The game
/// marks a character under World.Sync; a snapshot (deep copy) is taken under that lock and written outside it by one
/// writer thread, in order and coalescing per character (a newer snapshot replaces one still waiting). When: at once
/// on disconnect (<see cref="SaveNow"/>), <see cref="ChangeDelay"/> after an item or spell change
/// (<see cref="MarkChanged"/>), by the periodic save every <see cref="Period"/> for anything else (the position,
/// <see cref="MarkDirty"/>), and whatever is left when the world is disposed (Ctrl+C: World.Dispose saves all and
/// waits for the writer). A write that fails (the database locked by another program, a full disk) keeps its snapshot
/// queued: a Join still gets it (<see cref="Load"/>), and the writer tries again <see cref="RetryDelay"/> later,
/// waiting twice as long after each further failure (up to <see cref="MaxRetryDelay"/>); only at shutdown is it given
/// up, after <see cref="StopRetryTime"/>, with an error in the log. Characters saved together (<see cref="SaveTogether"/>: the two
/// sides of a trade) are linked until one write has stored all of them in one transaction (stageJ trade SPEC §2.5).
/// </summary>
public sealed class SaveScheduler : IDisposable
{
    public static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="Dispose"/> keeps retrying a write that fails before giving it up (Ctrl+C has to end).</summary>
    public static readonly TimeSpan StopRetryTime = TimeSpan.FromSeconds(5);

    private readonly ICharacterRepository _repository;
    private readonly Action<Character> _refresh;

    // World side, under World.Sync: live characters with unsaved changes and when each is due (MaxValue = periodic save).
    private readonly Dictionary<Guid, (Character Live, TimeSpan Due)> _dirty = new();
    private readonly List<Character> _ready = [];
    private TimeSpan _now;
    private TimeSpan? _nextPeriod;

    // Writer side, under _gate: the newest snapshot per character not yet written, in the order they were first queued,
    // and how often the write of a queued snapshot has failed so far (none = not tried yet).
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Character> _queued = new();
    private readonly Queue<Guid> _order = new();
    private readonly Dictionary<Guid, int> _failures = new();
    // The link groups of SaveTogether: each id of a group maps to the same set (merged transitively), until the group is written.
    private readonly Dictionary<Guid, HashSet<Guid>> _links = new();
    private readonly Thread _writer;
    private bool _stopping, _done;
    private long _giveUpAt; // Environment.TickCount64 from which a stopping writer gives a failing write up

    /// <param name="refresh">
    /// Called on the live character right before each snapshot (under World.Sync), for state kept elsewhere until then:
    /// the World writes the joined player's position into it.
    /// </param>
    public SaveScheduler(ICharacterRepository repository, Action<Character>? refresh = null)
    {
        _repository = repository;
        _refresh = refresh ?? (_ => { });
        _writer = new Thread(Write) { Name = "saves", IsBackground = true };
        _writer.Start();
    }

    /// <summary>Under World.Sync: <paramref name="c"/> changed in a way the periodic save is soon enough for (Move).</summary>
    public void MarkDirty(Character c) => Mark(c, TimeSpan.MaxValue);

    /// <summary>Under World.Sync: items or spells changed; saved <see cref="ChangeDelay"/> from now (or earlier if already due).</summary>
    public void MarkChanged(Character c) => Mark(c, _now + ChangeDelay);

    /// <summary>
    /// Under World.Sync: something about <paramref name="c"/> was already committed elsewhere (a mail with reward items); saved at the next world
    /// tick, so the state the caller records after this call (the reward marked given) is in that snapshot too.
    /// </summary>
    public void MarkUrgent(Character c) => Mark(c, _now);

    private void Mark(Character c, TimeSpan due)
    {
        if (c.Transient) return; // stage I: a bot's character is never saved (the one choke point of every save path)
        if (_dirty.TryGetValue(c.Id, out var d) && d.Due < due) due = d.Due;
        _dirty[c.Id] = (c, due);
    }

    /// <summary>Under World.Sync: snapshot now and queue it for the writer (disconnect).</summary>
    public void SaveNow(Character c)
    {
        if (c.Transient) return; // stage I: a bot's character is never saved
        _dirty.Remove(c.Id);
        _refresh(c);
        Enqueue(c.Clone());
    }

    /// <summary>
    /// Under World.Sync: <see cref="SaveNow"/> for each of <paramref name="cs"/>, linked: the writer stores the newest snapshot of every
    /// character of the group in one transaction (<see cref="ICharacterRepository.SaveMany"/>), so a crash keeps all of them or none
    /// (a trade's two sides). A newer snapshot of a linked character (its disconnect save) replaces the queued one and stays linked;
    /// a failed write keeps the whole group queued and linked for the retry.
    /// </summary>
    public void SaveTogether(params Character[] cs)
    {
        var snapshots = new List<Character>(cs.Length);
        foreach (var c in cs)
        {
            _dirty.Remove(c.Id);
            _refresh(c);
            snapshots.Add(c.Clone());
        }
        lock (_gate)
        {
            if (!_done)
            {
                var group = new HashSet<Guid>();
                foreach (var c in snapshots)
                    if (!group.Contains(c.Id)) group.UnionWith(_links.GetValueOrDefault(c.Id) ?? [c.Id]);
                foreach (var id in group) _links[id] = group;
                foreach (var c in snapshots) Enqueue(c); // under _gate (re-entered): the writer cannot pick one before all are queued
                return;
            }
        }
        if (Store(snapshots) is { } error) Log.Error("store", $"saving {Names(snapshots)} after shutdown failed: {error}; their changes are lost");
    }

    /// <summary>Under World.Sync: <see cref="SaveNow"/> for every character with unsaved changes.</summary>
    public void SaveAll()
    {
        _ready.AddRange(_dirty.Values.Select(d => d.Live));
        Flush(_ready);
    }

    /// <summary>World timer (<see cref="World.Update"/>, under World.Sync): queues the snapshots that are due.</summary>
    public void Update(TimeSpan now)
    {
        _now = now;
        _nextPeriod ??= now + Period;
        var periodic = now >= _nextPeriod;
        if (periodic) _nextPeriod = now + Period;
        foreach (var (live, due) in _dirty.Values)
            if (periodic || now >= due) _ready.Add(live);
        Flush(_ready);
    }

    private void Flush(List<Character> ready)
    {
        foreach (var c in ready) SaveNow(c);
        ready.Clear();
    }

    /// <summary>
    /// The newest saved state of a character: a snapshot still waiting for the writer (or for a retry after a failed
    /// write), else the repository's. Under World.Sync, so no new snapshot can come in between the two looks (a written
    /// one leaves the queue only after its commit).
    /// </summary>
    public Character? Load(Guid id)
    {
        lock (_gate)
            if (_queued.TryGetValue(id, out var waiting)) return waiting.Clone();
        return _repository.Find(id);
    }

    /// <summary>
    /// Waits until every queued snapshot is written or its latest write has failed (it stays queued for the retry).
    /// Ordinary callers release World.Sync first; <see cref="Commit{T}"/> deliberately holds it for its short transaction barrier.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
            while (_order.Any(id => !_failures.ContainsKey(id))) Monitor.Wait(_gate);
    }

    /// <summary>
    /// Under World.Sync: briefly blocks the world while all older character snapshots drain, then performs one
    /// external atomic write (for example shop state plus silver). The writer never takes World.Sync, so it can
    /// finish while this waits. An unsuccessful queued save refuses the transaction; its callback is not called.
    /// Callers prepare copies and publish live state only after a successful commit. Holding _gate across the
    /// callback prevents an old snapshot from being written after the external transaction.
    /// </summary>
    public T Commit<T>(Func<T> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        lock (_gate)
        {
            if (_stopping || _done) throw new InvalidOperationException("The save scheduler is stopping.");
            if (_failures.Count != 0) throw new InvalidOperationException("A character save is awaiting retry; the transaction was not started.");
        }
        SaveAll();
        Flush();
        lock (_gate)
        {
            if (_stopping || _done || _queued.Count != 0 || _order.Count != 0 || _failures.Count != 0)
                throw new InvalidOperationException("Character saves did not drain; the transaction was not started.");
            return write();
        }
    }

    /// <summary>
    /// Writes what is queued and stops the writer; a write that keeps failing is retried for <see cref="StopRetryTime"/>,
    /// then given up. Safe to call twice.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            _giveUpAt = Environment.TickCount64 + (long)StopRetryTime.TotalMilliseconds;
            Monitor.PulseAll(_gate);
        }
        _writer.Join();
    }

    private void Enqueue(Character snapshot)
    {
        lock (_gate)
        {
            // While stopping too, until the writer has drained the queue: it keeps the order per character.
            if (!_done)
            {
                if (!_queued.ContainsKey(snapshot.Id)) _order.Enqueue(snapshot.Id);
                _queued[snapshot.Id] = snapshot;
                _failures.Remove(snapshot.Id); // a new snapshot: not tried yet
                Monitor.PulseAll(_gate);
                return;
            }
        }
        // After Dispose (nothing should save that late): written right here rather than lost.
        if (Store([snapshot]) is { } error) Log.Error("store", $"saving '{snapshot.Name}' after shutdown failed: {error}; its changes are lost");
    }

    private void Write()
    {
        var wait = TimeSpan.Zero; // after a failed write: before the next one
        var lastWrite = 0L; // Environment.TickCount64
        while (true)
        {
            Guid id;
            List<Character> batch;
            int failures;
            lock (_gate)
            {
                // New snapshots do not cut the wait short (the database is the problem); a stop shortens it to RetryDelay.
                for (long left; (left = lastWrite + (long)(_stopping && wait > RetryDelay ? RetryDelay : wait).TotalMilliseconds
                                        - Environment.TickCount64) > 0;)
                    Monitor.Wait(_gate, (int)left);
                while (_order.Count == 0)
                {
                    if (_stopping)
                    {
                        _done = true;
                        return;
                    }
                    Monitor.Wait(_gate);
                }
                id = _order.Peek();
                // A linked character goes with its whole group (all queued: a link is dropped only when the group is written), id first.
                batch = [_queued[id], .. _links.GetValueOrDefault(id)?.Where(m => m != id && _queued.ContainsKey(m)).Select(m => _queued[m]) ?? []];
                failures = _failures.GetValueOrDefault(id);
            }
            var error = Store(batch);
            var givenUp = false;
            lock (_gate)
            {
                _order.Dequeue();
                var done = error is null || (givenUp = _stopping && Environment.TickCount64 >= _giveUpAt);
                var written = new HashSet<Guid>();
                foreach (var b in batch)
                {
                    // A newer snapshot came in while this one was written: it follows, with its link (a later SaveTogether may have merged it).
                    if (!ReferenceEquals(_queued[b.Id], b)) continue;
                    if (done)
                    {
                        _links.Remove(b.Id);
                        _queued.Remove(b.Id);
                        _failures.Remove(b.Id);
                        written.Add(b.Id);
                    }
                    else _failures[b.Id] = failures + 1; // kept: Join still loads it (Load), and it is tried again after the wait, at shutdown too
                }
                if (!written.Contains(id)) _order.Enqueue(id);
                if (batch.Count > 1 && written.Count > 0)
                {
                    // The group's other members written with it leave the queue too (the order of the rest is kept).
                    var rest = _order.Where(m => !written.Contains(m)).ToList();
                    _order.Clear();
                    foreach (var m in rest) _order.Enqueue(m);
                }
                wait = error is null ? TimeSpan.Zero : wait == TimeSpan.Zero ? RetryDelay : wait * 2 < MaxRetryDelay ? wait * 2 : MaxRetryDelay;
                lastWrite = Environment.TickCount64;
                Monitor.PulseAll(_gate);
            }
            var names = Names(batch);
            if (error is null)
            {
                if (failures > 0) Log.Info("store", $"saved {names} after {failures} failed attempt(s)");
            }
            else if (givenUp)
                Log.Error("store", $"saving {names} failed: {error}; given up at shutdown, the changes since the last save are lost");
            else if (failures == 0)
                Log.Error("store", $"saving {names} failed: {error}; kept and retried");
            else
                Log.Debug("store", $"saving {names} failed again (attempt {failures + 1}): {error}");
        }
    }

    /// <summary>"'A'", or "'A' and 'B' together" for a linked group.</summary>
    private static string Names(List<Character> batch) =>
        batch.Count == 1 ? $"'{batch[0].Name}'" : string.Join(" and ", batch.Select(c => $"'{c.Name}'")) + " together";

    /// <summary>
    /// Writes one snapshot, or a linked group in one transaction: null when done with it (saved, or a character was deleted meanwhile),
    /// else the error.
    /// </summary>
    private string? Store(List<Character> batch)
    {
        try
        {
            if (batch.Count > 1)
            {
                var stored = _repository.SaveMany(batch);
                if (stored == batch.Count) Log.Debug("store", $"saved {Names(batch)}");
                else Log.Warn("store", $"of {Names(batch)} {batch.Count - stored} not saved: deleted");
            }
            else if (_repository.Save(batch[0])) Log.Debug("store", $"saved '{batch[0].Name}'");
            else Log.Warn("store", $"'{batch[0].Name}' not saved: the character was deleted");
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
