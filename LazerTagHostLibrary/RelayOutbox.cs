using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LazerTagHostLibrary
{
    // One instance belongs to one connection. Each CMD signature gets one FIFO
    // OK or ERROR from the ESP32, including signatures in untracked packets.
    internal sealed class RelayOutbox
    {
        internal sealed class Write
        {
            public byte[] Bytes;
            public int Replies;
            public bool Rejected;
            public long ExpiresAt;
            public TaskCompletionSource<bool> Receipt;
        }

        private readonly Queue<Write> _queued = new Queue<Write>();
        private readonly Queue<Write> _awaiting = new Queue<Write>();
        private readonly object _sync = new object();
        private readonly Stopwatch _replyTimer = new Stopwatch();
        private bool _closed;

        internal bool TryQueue(byte[] bytes, int replies, int lifetimeMs, TaskCompletionSource<bool> receipt = null)
        {
            lock (_sync)
            {
                if (_closed || _queued.Count >= 64 || bytes.Length > 16384) return false;
                _queued.Enqueue(new Write
                {
                    Bytes = bytes, Replies = replies, Receipt = receipt,
                    ExpiresAt = Stopwatch.GetTimestamp() + (long)lifetimeMs * Stopwatch.Frequency / 1000
                });
                Monitor.PulseAll(_sync);
                return true;
            }
        }

        internal bool TryTake(out Write write, CancellationToken token)
        {
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                // Keep long IR transmissions out of the socket buffer until the
                // board makes progress. Queued countdowns can still expire here.
                if (!_closed && (_queued.Count == 0 || _awaiting.Count >= 4)) Monitor.Wait(_sync, 100);
                token.ThrowIfCancellationRequested();
                if (_closed || _queued.Count == 0 || _awaiting.Count >= 4)
                {
                    write = null;
                    return false;
                }
                write = _queued.Dequeue();
                return true;
            }
        }

        internal bool BeginWrite(Write write, bool tcp)
        {
            lock (_sync)
            {
                if (_closed || Stopwatch.GetTimestamp() >= write.ExpiresAt)
                {
                    write.Receipt?.TrySetResult(false);
                    return false;
                }
                if (tcp && write.Replies > 0)
                {
                    if (_awaiting.Count == 0) _replyTimer.Restart();
                    _awaiting.Enqueue(write);
                }
                return true;
            }
        }

        internal void Reply(bool accepted)
        {
            lock (_sync)
            {
                if (_awaiting.Count == 0) return;
                var write = _awaiting.Peek();
                if (!accepted) write.Rejected = true;
                if (--write.Replies == 0)
                {
                    _awaiting.Dequeue();
                    write.Receipt?.TrySetResult(!write.Rejected);
                    Monitor.PulseAll(_sync);
                }
                if (_awaiting.Count == 0) _replyTimer.Reset();
                else _replyTimer.Restart();
            }
        }

        internal void CheckReplyDeadline()
        {
            lock (_sync)
                if (_replyTimer.ElapsedMilliseconds >= 5000)
                    throw new IOException("The relay stopped acknowledging transmitted commands.");
        }

        internal void Close()
        {
            lock (_sync)
            {
                _closed = true;
                while (_queued.Count > 0) _queued.Dequeue().Receipt?.TrySetResult(false);
                while (_awaiting.Count > 0) _awaiting.Dequeue().Receipt?.TrySetResult(false);
                _replyTimer.Reset();
                Monitor.PulseAll(_sync);
            }
        }
    }
}
