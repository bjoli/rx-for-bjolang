// Ported from regex-automata/src/nfa/thompson/map.rs (rust-lang/regex). MIT
// OR Apache-2.0; see THIRD-PARTY.md.

namespace BjoRx.Nfa;

/// <summary>
/// A hash map of fixed size that forgets instead of growing: a collision
/// overwrites. It caches the states the UTF-8 compiler has already built, and
/// a forgotten entry only costs a duplicate state. Clearing bumps a version
/// rather than wiping the table, so clearing per class is cheap.
/// </summary>
internal sealed class Utf8BoundedMap
{
    private const ulong Prime = 1099511628211;
    private const ulong Init = 14695981039346656037;

    private struct Entry
    {
        public ushort Version;
        public Transition[] Key;
        public int Value;
    }

    private readonly int _capacity;
    private Entry[] _map = [];
    private ushort _version;

    public Utf8BoundedMap(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Clear()
    {
        if (_map.Length == 0)
        {
            _map = new Entry[_capacity];
            return;
        }
        _version++;
        if (_version == 0) _map = new Entry[_capacity];
    }

    public int Hash(List<Transition> key)
    {
        ulong h = Init;
        foreach (var t in key)
        {
            h = (h ^ t.Start) * Prime;
            h = (h ^ t.End) * Prime;
            h = (h ^ (ulong)t.Next) * Prime;
        }
        return (int)(h % (ulong)_map.Length);
    }

    public int? Get(List<Transition> key, int hash)
    {
        ref var entry = ref _map[hash];
        if (entry.Version != _version || entry.Key is null) return null;
        if (!entry.Key.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(key))) return null;
        return entry.Value;
    }

    public void Set(Transition[] key, int hash, int stateId)
    {
        _map[hash] = new Entry { Version = _version, Key = key, Value = stateId };
    }
}

/// <summary>
/// The same kind of map for the reverse compiler's suffix cache, keyed by the
/// state a byte range leads to and the range itself.
/// </summary>
internal sealed class Utf8SuffixMap
{
    private const ulong Prime = 1099511628211;
    private const ulong Init = 14695981039346656037;

    public readonly record struct Key(int From, byte Start, byte End);

    private struct Entry
    {
        public ushort Version;
        public bool Used;
        public Key Key;
        public int Value;
    }

    private readonly int _capacity;
    private Entry[] _map = [];
    private ushort _version;

    public Utf8SuffixMap(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Clear()
    {
        if (_map.Length == 0)
        {
            _map = new Entry[_capacity];
            return;
        }
        _version++;
        if (_version == 0) _map = new Entry[_capacity];
    }

    public int Hash(Key key)
    {
        ulong h = Init;
        h = (h ^ (ulong)key.From) * Prime;
        h = (h ^ key.Start) * Prime;
        h = (h ^ key.End) * Prime;
        return (int)(h % (ulong)_map.Length);
    }

    public int? Get(Key key, int hash)
    {
        ref var entry = ref _map[hash];
        if (!entry.Used || entry.Version != _version || entry.Key != key) return null;
        return entry.Value;
    }

    public void Set(Key key, int hash, int stateId)
    {
        _map[hash] = new Entry { Version = _version, Used = true, Key = key, Value = stateId };
    }
}
