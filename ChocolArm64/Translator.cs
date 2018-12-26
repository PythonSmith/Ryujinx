using ChocolArm64.Decoders;
using ChocolArm64.Events;
using ChocolArm64.Memory;
using ChocolArm64.State;
using ChocolArm64.Translation;
using System;
using System.Reflection.Emit;
using System.Threading;

namespace ChocolArm64
{
    public class Translator
    {
        private const int BackgroundTranslatorThreads = 1;

        private MemoryManager _memory;

        private CpuThreadState _dummyThreadState;

        private TranslatorCache _cache;
        private TranslatorQueue _queue;

        private Thread[] _backgroundTranslators;

        public event EventHandler<CpuTraceEventArgs> CpuTrace;

        public bool EnableCpuTrace { get; set; }

        private volatile int _threadCount;

        public Translator(MemoryManager memory)
        {
            _memory = memory;

            _dummyThreadState = new CpuThreadState();

            _dummyThreadState.Running = false;

            _dummyThreadState.ExecutionMode = ExecutionMode.AArch64;

            _cache = new TranslatorCache();
            _queue = new TranslatorQueue();

            _backgroundTranslators = new Thread[BackgroundTranslatorThreads];

            for (int index = 0; index < _backgroundTranslators.Length; index++)
            {
                _backgroundTranslators[index] = new Thread(TranslateQueuedSubs);
                _backgroundTranslators[index].Start();
            }
        }

        internal void ExecuteSubroutine(CpuThread thread, long position)
        {
            if (Interlocked.Increment(ref _threadCount) == 1)
            {
                for (int index = 0; index < BackgroundTranslatorThreads; index++)
                {
                    Thread _backgroundTranslator = new Thread(TranslateQueuedSubs);

                    _backgroundTranslator.Start();
                }
            }

            //TODO: Both the execute A32/A64 methods should be merged on the future,
            //when both ISAs are implemented with the interpreter and JIT.
            //As of now, A32 only has a interpreter and A64 a JIT.
            CpuThreadState state  = thread.ThreadState;
            MemoryManager  memory = thread.Memory;

            if (state.ExecutionMode == ExecutionMode.AArch32)
            {
                ExecuteSubroutineA32(state, memory);
            }
            else
            {
                ExecuteSubroutineA64(state, memory, position);
            }

            if (Interlocked.Decrement(ref _threadCount) == 0)
            {
                _queue.ForceSignal();
            }
        }

        private void ExecuteSubroutineA32(CpuThreadState state, MemoryManager memory)
        {
            do
            {
                OpCode64 opCode = Decoder.DecodeOpCode(state, memory, state.R15);

                opCode.Interpreter(state, memory, opCode);
            }
            while (state.R15 != 0 && state.Running);
        }

        private void ExecuteSubroutineA64(CpuThreadState state, MemoryManager memory, long position)
        {
            do
            {
                if (EnableCpuTrace)
                {
                    CpuTrace?.Invoke(this, new CpuTraceEventArgs(position));
                }

                if (!_cache.TryGetSubroutine(position, out TranslatedSub subroutine))
                {
                    subroutine = TranslateLowCq(state, memory, position);
                }

                if (subroutine.ShouldReJit())
                {
                    _queue.Enqueue(new TranslatorQueueItem(
                        position,
                        0,
                        TranslationPriority.Medium,
                        TranslationCodeQuality.High));
                }

                position = subroutine.Execute(state, memory);
            }
            while (position != 0 && state.Running);
        }

        private void TranslateQueuedSubs()
        {
            while (_threadCount != 0)
            {
                if (_queue.TryDequeue(out TranslatorQueueItem item))
                {
                    if (_cache.TryGetSubroutine(item.Position, out TranslatedSub subroutine) &&
                        item.TranslationCq <= subroutine.TranslationCq &&
                        item.Priority      != TranslationPriority.Low)
                    {
                        continue;
                    }

                    if (item.TranslationCq == TranslationCodeQuality.Low)
                    {
                        TranslateLowCq(_dummyThreadState, _memory, item.Position, item.Depth);
                    }
                    else
                    {
                        TranslateHighCq(_dummyThreadState, _memory, item.Position, item.Depth);
                    }
                }
                else
                {
                    _queue.WaitForItems();
                }
            }
        }

        private TranslatedSub TranslateLowCq(CpuThreadState state, MemoryManager memory, long position, int depth = 0)
        {
            Block block = Decoder.DecodeBasicBlock(state, memory, position);

            ILEmitterCtx context = new ILEmitterCtx(_cache, _queue, block, depth);

            string subName = GetSubroutineName(position);

            ILMethodBuilder ilMthdBuilder = new ILMethodBuilder(context.GetILBlocks(), subName);

            TranslatedSub subroutine = ilMthdBuilder.GetSubroutine(TranslationCodeQuality.Low);

            if (depth != 0)
            {
                ForceAheadOfTimeCompilation(subroutine);
            }

            _cache.AddOrUpdate(position, subroutine, block.OpCodes.Count);

            return subroutine;
        }

        private void TranslateHighCq(CpuThreadState state, MemoryManager memory, long position, int depth)
        {
            Block graph = Decoder.DecodeSubroutine(_cache, state, memory, position);

            ILEmitterCtx context = new ILEmitterCtx(_cache, _queue, graph, depth);

            ILBlock[] ilBlocks = context.GetILBlocks();

            string subName = GetSubroutineName(position);

            ILMethodBuilder ilMthdBuilder = new ILMethodBuilder(ilBlocks, subName);

            TranslatedSub subroutine = ilMthdBuilder.GetSubroutine(TranslationCodeQuality.High);

            int ilOpCount = 0;

            foreach (ILBlock ilBlock in ilBlocks)
            {
                ilOpCount += ilBlock.Count;
            }

            _cache.AddOrUpdate(position, subroutine, ilOpCount);

            ForceAheadOfTimeCompilation(subroutine);

            //Mark all methods that calls this method for ReJiting,
            //since we can now call it directly which is faster.
            if (_cache.TryGetSubroutine(position, out TranslatedSub oldSub))
            {
                foreach (long callerPos in oldSub.GetCallerPositions())
                {
                    _queue.Enqueue(new TranslatorQueueItem(
                        callerPos,
                        depth,
                        TranslationPriority.Low,
                        TranslationCodeQuality.High));
                }
            }
        }

        private string GetSubroutineName(long position)
        {
            return $"Sub{position:x16}";
        }

        private int GetGraphInstCount(Block[] graph)
        {
            int size = 0;

            foreach (Block block in graph)
            {
                size += block.OpCodes.Count;
            }

            return size;
        }

        private void ForceAheadOfTimeCompilation(TranslatedSub subroutine)
        {
            subroutine.Execute(_dummyThreadState, null);
        }
    }
}