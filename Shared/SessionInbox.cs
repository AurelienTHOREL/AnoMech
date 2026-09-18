using System.Collections.Generic;

namespace AnoMech.Network;

internal enum InboxResult { Queued, StaleSource, Full }

internal sealed class SessionInbox<T>(int maxCount, int maxBytes)
{
    private readonly object gate = new();
    private readonly Queue<(T Item, int Bytes)> queue = new();
    private object? source;
    private int bytes;

    public void SetSource(object? value)
    {
        lock (gate)
        {
            source = value;
            queue.Clear();
            bytes = 0;
        }
    }

    // Past half full, a caller can shed messages a later one supersedes before the hard cap is
    // reached and something that can't be skipped has nowhere to go.
    public bool IsBacklogged
    {
        get { lock (gate) return queue.Count >= maxCount / 2 || bytes >= maxBytes / 2; }
    }

    public InboxResult TryEnqueue(object sender, T item, int size)
    {
        lock (gate)
        {
            if (!ReferenceEquals(sender, source)) return InboxResult.StaleSource;
            if (size < 0 || queue.Count >= maxCount || size > maxBytes - bytes) return InboxResult.Full;
            queue.Enqueue((item, size));
            bytes += size;
            return InboxResult.Queued;
        }
    }

    public bool TryDequeue(out T item)
    {
        lock (gate)
        {
            if (!queue.TryDequeue(out var entry)) { item = default!; return false; }
            bytes -= entry.Bytes;
            item = entry.Item;
            return true;
        }
    }
}
