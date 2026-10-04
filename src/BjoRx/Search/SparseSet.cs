// Ported from regex-automata/src/util/sparse_set.rs (rust-lang/regex). MIT
// OR Apache-2.0; see THIRD-PARTY.md.

namespace BjoRx.Search;

/// <summary>
/// A set of state ids below a fixed capacity, with constant-time insert,
/// membership and clear, that iterates in insertion order. The order is what
/// carries match priority in the NFA simulations.
/// </summary>
/// <remarks>
/// Briggs and Torczon's representation: <c>_dense</c> holds the members in
/// order and <c>_sparse[id]</c> says where in it <c>id</c> would be. Neither
/// array needs to be cleared, because membership checks both.
/// </remarks>
public sealed class SparseSet
{
    private int[] _dense;
    private int[] _sparse;

    public int Count { get; private set; }

    public SparseSet(int capacity)
    {
        _dense = new int[capacity];
        _sparse = new int[capacity];
    }

    public int Capacity => _dense.Length;

    public void Resize(int capacity)
    {
        _dense = new int[capacity];
        _sparse = new int[capacity];
        Count = 0;
    }

    public bool IsEmpty => Count == 0;

    /// <summary>Adds <paramref name="id"/>; false if it was already there.</summary>
    public bool Insert(int id)
    {
        if (Contains(id)) return false;
        _dense[Count] = id;
        _sparse[id] = Count;
        Count++;
        return true;
    }

    public bool Contains(int id)
    {
        int i = _sparse[id];
        return i < Count && _dense[i] == id;
    }

    public void Clear() => Count = 0;

    public int this[int index] => _dense[index];

    public ReadOnlySpan<int> AsSpan() => _dense.AsSpan(0, Count);
}
