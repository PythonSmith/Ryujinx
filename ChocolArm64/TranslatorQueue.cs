using System.Collections.Concurrent;
using System.Threading;

namespace ChocolArm64
{
    class TranslatorQueue
    {
        private ConcurrentStack<TranslatorQueueItem>[] _translationQueue;

        private AutoResetEvent _queueDataReceivedEvent;

        public TranslatorQueue()
        {
            _translationQueue = new ConcurrentStack<TranslatorQueueItem>[(int)TranslationPriority.Count];

            for (int prio = 0; prio < _translationQueue.Length; prio++)
            {
                _translationQueue[prio] = new ConcurrentStack<TranslatorQueueItem>();
            }

            _queueDataReceivedEvent = new AutoResetEvent(false);
        }

        public void Enqueue(TranslatorQueueItem item)
        {
            _translationQueue[(int)item.Priority].Push(item);

            _queueDataReceivedEvent.Set();
        }

        public bool TryDequeue(out TranslatorQueueItem item)
        {
            for (int prio = 0; prio < (int)TranslationPriority.Count; prio++)
            {
                if (_translationQueue[prio].TryPop(out item))
                {
                    return true;
                }
            }

            item = default(TranslatorQueueItem);

            return false;
        }

        public void WaitForItems()
        {
            _queueDataReceivedEvent.WaitOne();
        }

        public void ForceSignal()
        {
            _queueDataReceivedEvent.Set();
        }
    }
}