namespace Host2VMRelay;

/// <summary>A shared byte ceiling for independently bounded queues. Release only successful reservations.</summary>
internal sealed class ByteBudget(int capacity)
{
    private int used;
    internal int Used => Volatile.Read(ref used);
    internal bool TryReserve(int bytes)
    {
        if (bytes <= 0 || bytes > capacity) return false;
        int current;
        do
        {
            current = Volatile.Read(ref used);
            if (bytes > capacity - current) return false;
        } while (Interlocked.CompareExchange(ref used, current + bytes, current) != current);
        return true;
    }
    internal void Release(int bytes) => Interlocked.Add(ref used, -bytes);
}
