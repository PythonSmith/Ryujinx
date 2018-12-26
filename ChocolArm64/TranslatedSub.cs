using ChocolArm64.Memory;
using ChocolArm64.State;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ChocolArm64
{
    class TranslatedSub
    {
        private const int CallCountForReJit = 250;

        private delegate long ArmSubroutine(CpuThreadState register, MemoryManager memory);

        private ArmSubroutine _execDelegate;

        public static int StateArgIdx  { get; private set; }
        public static int MemoryArgIdx { get; private set; }

        public static Type[] FixedArgTypes { get; private set; }

        public DynamicMethod Method { get; private set; }

        public ReadOnlyCollection<Register> SubArgs { get; private set; }

        private HashSet<long> _callers;

        public TranslationCodeQuality TranslationCq { get; private set; }

        private int _callCount;

        private bool _needsReJit;

        public TranslatedSub(DynamicMethod method, List<Register> subArgs, TranslationCodeQuality translationCq)
        {
            Method  = method                ?? throw new ArgumentNullException(nameof(method));;
            SubArgs = subArgs?.AsReadOnly() ?? throw new ArgumentNullException(nameof(subArgs));

            TranslationCq = translationCq;

            _callers = new HashSet<long>();

            PrepareDelegate();
        }

        static TranslatedSub()
        {
            MethodInfo mthdInfo = typeof(ArmSubroutine).GetMethod("Invoke");

            ParameterInfo[] Params = mthdInfo.GetParameters();

            FixedArgTypes = new Type[Params.Length];

            for (int index = 0; index < Params.Length; index++)
            {
                Type paramType = Params[index].ParameterType;

                FixedArgTypes[index] = paramType;

                if (paramType == typeof(CpuThreadState))
                {
                    StateArgIdx = index;
                }
                else if (paramType == typeof(MemoryManager))
                {
                    MemoryArgIdx = index;
                }
            }
        }

        private void PrepareDelegate()
        {
            string name = $"{Method.Name}_Dispatch";

            DynamicMethod mthd = new DynamicMethod(name, typeof(long), FixedArgTypes);

            ILGenerator generator = mthd.GetILGenerator();

            generator.EmitLdargSeq(FixedArgTypes.Length);

            foreach (Register reg in SubArgs)
            {
                generator.EmitLdarg(StateArgIdx);

                generator.Emit(OpCodes.Ldfld, reg.GetField());
            }

            generator.Emit(OpCodes.Call, Method);
            generator.Emit(OpCodes.Ret);

            _execDelegate = (ArmSubroutine)mthd.CreateDelegate(typeof(ArmSubroutine));
        }

        public long Execute(CpuThreadState threadState, MemoryManager memory)
        {
            return _execDelegate(threadState, memory);
        }

        public bool ShouldReJit()
        {
            if (TranslationCq == TranslationCodeQuality.High || _callCount++ != CallCountForReJit)
            {
                return false;
            }

            return true;
        }

        public void AddCaller(long position)
        {
            lock (_callers)
            {
                _callers.Add(position);
            }
        }

        public long[] GetCallerPositions()
        {
            lock (_callers)
            {
                return _callers.ToArray();
            }
        }
    }
}