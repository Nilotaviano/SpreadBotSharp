using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    //Source: https://stackoverflow.com/a/23415880
    public class SemaphoreQueue
    {
        private readonly SemaphoreSlim semaphore;
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> queue =
            new ConcurrentQueue<TaskCompletionSource<bool>>();
        private bool stopped = false;

        public int CurrentCount => semaphore.CurrentCount;

        public SemaphoreQueue(int initialCount)
        {
            semaphore = new SemaphoreSlim(initialCount);
        }
        public SemaphoreQueue(int initialCount, int maxCount)
        {
            semaphore = new SemaphoreSlim(initialCount, maxCount);
        }
        public void Wait()
        {
            WaitAsync().Wait();
        }
        public Task<bool> WaitAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            queue.Enqueue(tcs);
            semaphore.WaitAsync().ContinueWith(t =>
            {
                if (queue.TryDequeue(out TaskCompletionSource<bool> popped))
                    popped.SetResult(!stopped);
            });
            return tcs.Task;
        }
        public void Release()
        {
            semaphore.Release();
        }

        public void ClearAndStopQueue()
        {
            stopped = true;
            queue.Clear();
        }
    }
}
