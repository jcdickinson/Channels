// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Channels
{
    public class Channel : IReadableChannel, IWritableChannel
    {
        private static readonly Action _awaitableIsCompleted = () => { };
        private static readonly Action _awaitableIsNotCompleted = () => { };

        private static Task _completedTask = Task.FromResult(0);

        private readonly MemoryPool _pool;

        private Action _awaitableState;

        private MemoryBlockSegment _head;
        private MemoryBlockSegment _tail;

        private bool _completedWriting;
        private bool _completedReading;

        private int _consumingState;
        private int _producingState;
        private object _sync = new object();

        // REVIEW: This object might be getting a little big :)
        private readonly TaskCompletionSource<object> _readingTcs = new TaskCompletionSource<object>();
        private readonly TaskCompletionSource<object> _writingTcs = new TaskCompletionSource<object>();
        private readonly TaskCompletionSource<object> _startingReadingTcs = new TaskCompletionSource<object>();
        private readonly TaskCompletionSource<object> _disposedTcs = new TaskCompletionSource<object>();

        public Channel(MemoryPool pool)
        {
            _pool = pool;
            _awaitableState = _awaitableIsNotCompleted;
        }

        public Task ChannelComplete => _disposedTcs.Task;

        public Task ReadingStarted => _startingReadingTcs.Task;

        public Task WriterCompleted => _readingTcs.Task;
        public Task ReaderCompleted => _writingTcs.Task;

        Task IReadableChannel.Completion => _readingTcs.Task;

        Task IWritableChannel.Completion => _writingTcs.Task;

        public bool IsCompleted => ReferenceEquals(_awaitableState, _awaitableIsCompleted);

        public WritableBuffer Alloc(int minimumSize = 0)
        {
            if (Interlocked.CompareExchange(ref _producingState, 1, 0) != 0)
            {
                throw new InvalidOperationException("Already producing.");
            }

            MemoryBlockSegment segment = null;

            if (_tail != null && !_tail.ReadOnly)
            {
                // Try to return the tail so the calling code can append to it
                int remaining = _tail.Block.Data.Length - _tail.End;

                if (minimumSize <= remaining)
                {
                    segment = _tail;
                }
            }

            if (segment == null && minimumSize > 0)
            {
                // We're out of tail space so lease a new segment only if the requested size > 0
                segment = new MemoryBlockSegment(_pool.Lease());
            }

            lock (_sync)
            {
                if (_head == null)
                {
                    _head = segment;
                }
                else if (segment != null && segment != _tail)
                {
                    // Append the segment to the tail if it's non-null
                    Volatile.Write(ref _tail.Next, segment);
                    _tail = segment;
                }

                return new WritableBuffer(this, _pool, segment);
            }
        }

        internal void Append(WritableBuffer buffer)
        {
            lock (_sync)
            {
                if (Interlocked.CompareExchange(ref _producingState, 0, 1) != 1)
                {
                    throw new InvalidOperationException("No ongoing producing operation to complete.");
                }

                if (buffer.IsDefault)
                {
                    // REVIEW: Should we signal the completion?
                    return;
                }

                if (_head == null)
                {
                    // Update the head to point to the head of the buffer. This
                    // happens if we called alloc(0) then write
                    _head = buffer.Head;
                    _head.Start = buffer.HeadIndex;
                }
                // If buffer.Head == tail it means we appended data to the tail
                else if (_tail != null && buffer.Head != _tail)
                {
                    // If we have a tail point next to the head of the buffer
                    Volatile.Write(ref _tail.Next, buffer.Head);
                }

                // Always update tail to the buffer's tail
                _tail = buffer.Tail;
                _tail.End = buffer.TailIndex;
            }
        }

        internal Task CompleteWriteAsync()
        {
            lock (_sync)
            {
                Complete();

                // Apply back pressure here
                return _completedTask;
            }
        }

        private void Complete()
        {
            var awaitableState = Interlocked.Exchange(
                ref _awaitableState,
                _awaitableIsCompleted);

            if (!ReferenceEquals(awaitableState, _awaitableIsCompleted) &&
                !ReferenceEquals(awaitableState, _awaitableIsNotCompleted))
            {
                awaitableState();
            }
        }

        private ReadableBuffer Read()
        {
            if (Interlocked.CompareExchange(ref _consumingState, 1, 0) != 0)
            {
                throw new InvalidOperationException("Already consuming.");
            }

            return new ReadableBuffer(this, new ReadCursor(_head), new ReadCursor(_tail, _tail?.End ?? 0));
        }

        internal void EndRead(ReadCursor end)
        {
            EndRead(end, end);
        }

        internal void EndRead(
            ReadCursor consumed,
            ReadCursor examined)
        {
            MemoryBlockSegment returnStart = null;
            MemoryBlockSegment returnEnd = null;

            lock (_sync)
            {
                if (!consumed.IsDefault)
                {
                    returnStart = _head;
                    returnEnd = consumed.Segment;
                    _head = consumed.Segment;
                    _head.Start = consumed.Index;
                }

                if (!examined.IsDefault &&
                    examined.IsEnd &&
                    WriterCompleted.Status == TaskStatus.WaitingForActivation)
                {
                    Interlocked.CompareExchange(
                        ref _awaitableState,
                        _awaitableIsNotCompleted,
                        _awaitableIsCompleted);
                }
            }

            while (returnStart != returnEnd)
            {
                var returnSegment = returnStart;
                returnStart = returnStart.Next;
                returnSegment.Dispose();
            }

            if (Interlocked.CompareExchange(ref _consumingState, 0, 1) != 1)
            {
                throw new InvalidOperationException("No ongoing consuming operation to complete.");
            }
        }

        public void CompleteWriting(Exception error = null)
        {
            lock (_sync)
            {
                _completedWriting = true;

                if (error != null)
                {
                    _readingTcs.TrySetResult(error);
                }
                else
                {
                    _readingTcs.TrySetResult(null);
                }

                Complete();

                if (_completedReading)
                {
                    Dispose();
                }
            }
        }

        public void CompleteReading(Exception error = null)
        {
            lock (_sync)
            {
                _completedReading = true;

                if (error != null)
                {
                    _writingTcs.TrySetResult(error);
                }
                else
                {
                    _writingTcs.TrySetResult(null);
                }

                if (_completedWriting)
                {
                    Dispose();
                }
            }
        }

        public ChannelAwaitable ReadAsync()
        {
            return new ChannelAwaitable(this);
        }

        internal void OnCompleted(Action continuation)
        {
            _startingReadingTcs.TrySetResult(null);

            var awaitableState = Interlocked.CompareExchange(
                ref _awaitableState,
                continuation,
                _awaitableIsNotCompleted);

            if (ReferenceEquals(awaitableState, _awaitableIsNotCompleted))
            {
                return;
            }
            else if (ReferenceEquals(awaitableState, _awaitableIsCompleted))
            {
                // Dispatch here to avoid stack diving
                // Task.Run(continuation);
                continuation();
            }
            else
            {
                _readingTcs.SetException(new InvalidOperationException("Concurrent reads are not supported."));

                Interlocked.Exchange(
                    ref _awaitableState,
                    _awaitableIsCompleted);

                Task.Run(continuation);
                Task.Run(awaitableState);
            }
        }

        internal void UnsafeOnCompleted(Action continuation)
        {
            OnCompleted(continuation);
        }

        internal ReadableBuffer GetResult()
        {
            if (!IsCompleted)
            {
                throw new InvalidOperationException("can't GetResult unless completed");
            }

            var error = _readingTcs.Task.Exception?.InnerException;
            if (error != null)
            {
                throw error;
            }

            return Read();
        }

        private void Dispose()
        {
            Debug.Assert(_completedWriting, "Not completed writing");
            Debug.Assert(_completedReading, "Not completed reading");

            lock (_sync)
            {
                // Return all segments
                var segment = _head;
                while (segment != null)
                {
                    var returnSegment = segment;
                    segment = segment.Next;

                    returnSegment.Dispose();
                }

                _head = null;
                _tail = null;

                _disposedTcs.TrySetResult(null);
            }
        }
    }
}
