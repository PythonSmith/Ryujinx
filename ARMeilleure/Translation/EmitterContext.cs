using ARMeilleure.Diagnostics;
using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.State;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;

using static ARMeilleure.IntermediateRepresentation.OperandHelper;

namespace ARMeilleure.Translation
{
    using PTC;

    class EmitterContext
    {
        private Dictionary<Operand, BasicBlock> _irLabels;

        private IntrusiveList<BasicBlock> _irBlocks;

        private BasicBlock _irBlock;

        private bool _needsNewBlock;

        public EmitterContext()
        {
            _irLabels = new Dictionary<Operand, BasicBlock>();

            _irBlocks = new IntrusiveList<BasicBlock>();

            _needsNewBlock = true;
        }

        public Operand Add(Operand op1, Operand op2)
        {
            return Add(Instruction.Add, Local(op1.Type), op1, op2);
        }

        public Operand BitwiseAnd(Operand op1, Operand op2)
        {
            return Add(Instruction.BitwiseAnd, Local(op1.Type), op1, op2);
        }

        public Operand BitwiseExclusiveOr(Operand op1, Operand op2)
        {
            return Add(Instruction.BitwiseExclusiveOr, Local(op1.Type), op1, op2);
        }

        public Operand BitwiseNot(Operand op1)
        {
            return Add(Instruction.BitwiseNot, Local(op1.Type), op1);
        }

        public Operand BitwiseOr(Operand op1, Operand op2)
        {
            return Add(Instruction.BitwiseOr, Local(op1.Type), op1, op2);
        }

        public void Branch(Operand label)
        {
            Add(Instruction.Branch, null);

            BranchToLabel(label);
        }

        public void BranchIf(Operand label, Operand op1, Operand op2, Comparison comp)
        {
            Add(Instruction.BranchIf, null, op1, op2, Const((int)comp));

            BranchToLabel(label);
        }

        public void BranchIfFalse(Operand label, Operand op1)
        {
            BranchIf(label, op1, Const(op1.Type, 0), Comparison.Equal);
        }

        public void BranchIfTrue(Operand label, Operand op1)
        {
            BranchIf(label, op1, Const(op1.Type, 0), Comparison.NotEqual);
        }

        public Operand ByteSwap(Operand op1)
        {
            return Add(Instruction.ByteSwap, Local(op1.Type), op1);
        }

        public Operand Call(MethodInfo info, params Operand[] callArgs)
        {
            if (Ptc.State == PtcState.Disabled)
            {
                IntPtr funcPtr = Delegates.GetDelegateFuncPtr(info);

                OperandType returnType = GetOperandType(info.ReturnType);

                Symbols.Add((ulong)funcPtr.ToInt64(), info.Name);

                return Call(Const(funcPtr.ToInt64()), returnType, callArgs);
            }
            else
            {
                int index = Delegates.GetDelegateIndex(info);

                IntPtr funcPtr = Delegates.GetDelegateFuncPtrByIndex(index);

                OperandType returnType = GetOperandType(info.ReturnType);

                Symbols.Add((ulong)funcPtr.ToInt64(), info.Name);

                return Call(Const(funcPtr.ToInt64(), true, index), returnType, callArgs);
            }
        }

        public Operand Call(Expression<Action> expression)
        {
            if (!(expression.Body is MethodCallExpression expr))
            {
                throw new NotImplementedException("Only call is currently supported");
            }

            Debug.Assert(expr.Object is null);
            var args = new Operand[expr.Arguments.Count];
            for (int i = 0; i < args.Length; i++)
            {
                var arg = expr.Arguments[i];
                if (arg is MethodCallExpression methodCallExpression)
                {
                    if (methodCallExpression.Object is System.Linq.Expressions.MemberExpression member)
                    {
                        if (!TryGetMemberOfConstObject<Operand>(member, out var a))
                        {
                            throw new InvalidOperationException($"Could not get value from {member}");
                        }
                        args[i] = a;
                    }
                    else
                    {
                        throw new NotImplementedException($"Method call expression is not implemented for object expression {methodCallExpression.Object}");
                    }
                }
                else if (arg is MemberExpression member)
                {
                    if (!TryGetMemberOfConstObject<object>(member, out var obj))
                    {
                        throw new ArgumentException(paramName: nameof(expression), message: $"Could not get value from {member}");
                    }

                    Type argumentType;
                    if (member.Member is PropertyInfo property)
                    {
                        argumentType = property.PropertyType;
                    }
                    else if (member.Member is FieldInfo field)
                    {
                        argumentType = field.FieldType;
                    }
                    else
                    {
                        throw new NotImplementedException($"MemberInfo of type {member} is not implemented");
                    }

                    args[i] = Type.GetTypeCode(argumentType) switch
                    {
                        TypeCode.Boolean => Const((bool)obj),
                        TypeCode.Int32 => Const((int)obj),
                        TypeCode.UInt32 => Const((uint)obj),
                        TypeCode.UInt64 => Const((ulong)obj),
                        TypeCode.Int64 => Const((long)obj),
                        TypeCode.Single => ConstF((float)obj),
                        TypeCode.Double => ConstF((double)obj),
                        _ => throw new NotImplementedException($"Type {argumentType} can not be represented as a constant operand"),
                    };
                }
                else
                {
                    throw new NotImplementedException($"Unsupported expression {arg} for argument at position {i}");
                }
            }

            return Call(expr.Method, args);
        }

        private static bool TryGetMemberOfConstObject<T>(MemberExpression memberExpression, out T result)
        {
            var capturedObject = (memberExpression.Expression as ConstantExpression).Value;

            if (capturedObject is null)
            {
                result = default;
                return false;
            }

            var (assigned, obj) = memberExpression.Member switch
            {
                PropertyInfo property => (true, property.GetValue(capturedObject)),
                FieldInfo field => (true, field.GetValue(capturedObject)),
                _ => (false, null)
            };

            result = assigned ? (T)obj : default;
            return assigned;
        }
        private static OperandType GetOperandType(Type type)
        {
            if (type == typeof(bool)   || type == typeof(byte)  ||
                type == typeof(char)   || type == typeof(short) ||
                type == typeof(int)    || type == typeof(sbyte) ||
                type == typeof(ushort) || type == typeof(uint))
            {
                return OperandType.I32;
            }
            else if (type == typeof(long) || type == typeof(ulong))
            {
                return OperandType.I64;
            }
            else if (type == typeof(double))
            {
                return OperandType.FP64;
            }
            else if (type == typeof(float))
            {
                return OperandType.FP32;
            }
            else if (type == typeof(V128))
            {
                return OperandType.V128;
            }
            else if (type == typeof(void))
            {
                return OperandType.None;
            }
            else
            {
                throw new ArgumentException($"Invalid type \"{type.Name}\".");
            }
        }

        public Operand Call(Operand address, OperandType returnType, params Operand[] callArgs)
        {
            Operand[] args = new Operand[callArgs.Length + 1];

            args[0] = address;

            Array.Copy(callArgs, 0, args, 1, callArgs.Length);

            if (returnType != OperandType.None)
            {
                return Add(Instruction.Call, Local(returnType), args);
            }
            else
            {
                return Add(Instruction.Call, null, args);
            }
        }

        public void Tailcall(Operand address, params Operand[] callArgs)
        {
            Operand[] args = new Operand[callArgs.Length + 1];

            args[0] = address;

            Array.Copy(callArgs, 0, args, 1, callArgs.Length);

            Add(Instruction.Tailcall, null, args);

            _needsNewBlock = true;
        }

        public Operand CompareAndSwap(Operand address, Operand expected, Operand desired)
        {
            return Add(Instruction.CompareAndSwap, Local(desired.Type), address, expected, desired);
        }

        public Operand CompareAndSwap16(Operand address, Operand expected, Operand desired)
        {
            return Add(Instruction.CompareAndSwap16, Local(OperandType.I32), address, expected, desired);
        }

        public Operand CompareAndSwap8(Operand address, Operand expected, Operand desired)
        {
            return Add(Instruction.CompareAndSwap8, Local(OperandType.I32), address, expected, desired);
        }

        public Operand ConditionalSelect(Operand op1, Operand op2, Operand op3)
        {
            return Add(Instruction.ConditionalSelect, Local(op2.Type), op1, op2, op3);
        }

        public Operand ConvertI64ToI32(Operand op1)
        {
            if (op1.Type != OperandType.I64)
            {
                throw new ArgumentException($"Invalid operand type \"{op1.Type}\".");
            }

            return Add(Instruction.ConvertI64ToI32, Local(OperandType.I32), op1);
        }

        public Operand ConvertToFP(OperandType type, Operand op1)
        {
            return Add(Instruction.ConvertToFP, Local(type), op1);
        }

        public Operand ConvertToFPUI(OperandType type, Operand op1)
        {
            return Add(Instruction.ConvertToFPUI, Local(type), op1);
        }

        public Operand Copy(Operand op1)
        {
            return Add(Instruction.Copy, Local(op1.Type), op1);
        }

        public Operand Copy(Operand dest, Operand op1)
        {
            if (dest.Kind != OperandKind.Register)
            {
                throw new ArgumentException($"Invalid dest operand kind \"{dest.Kind}\".");
            }

            return Add(Instruction.Copy, dest, op1);
        }

        public Operand CountLeadingZeros(Operand op1)
        {
            return Add(Instruction.CountLeadingZeros, Local(op1.Type), op1);
        }

        public Operand Divide(Operand op1, Operand op2)
        {
            return Add(Instruction.Divide, Local(op1.Type), op1, op2);
        }

        public Operand DivideUI(Operand op1, Operand op2)
        {
            return Add(Instruction.DivideUI, Local(op1.Type), op1, op2);
        }

        public Operand ICompare(Operand op1, Operand op2, Comparison comp)
        {
            return Add(Instruction.Compare, Local(OperandType.I32), op1, op2, Const((int)comp));
        }

        public Operand ICompareEqual(Operand op1, Operand op2)
        {
            return ICompare(op1, op2, Comparison.Equal);
        }

        public Operand ICompareGreater(Operand op1, Operand op2)
        {
            return ICompare(op1, op2, Comparison.Greater);
        }

        public Operand ICompareGreaterOrEqual(Operand op1, Operand op2)
        {
            return ICompare(op1, op2, Comparison.GreaterOrEqual);
        }

        public Operand ICompareGreaterOrEqualUI(Operand op1, Operand op2)
        {
            return ICompare(op1, op2, Comparison.GreaterOrEqualUI);
        }

        public Operand ICompareGreaterUI(Operand op1, Operand op2)
        {
            return ICompare(op1, op2, Comparison.GreaterUI);
        }

        public Operand ICompareLess(Operand op1, Operand op2)
        {
            return ICompare(op1, op2, Comparison.Less);
        }

        public Operand ICompareLessOrEqual(Operand op1, Operand op2)
        {
            return ICompare(op1, op2, Comparison.LessOrEqual);
        }

        public Operand ICompareLessOrEqualUI(Operand op1, Operand op2)
        {
            return ICompare(op1, op2, Comparison.LessOrEqualUI);
        }

        public Operand ICompareLessUI(Operand op1, Operand op2)
        {
            return ICompare(op1, op2, Comparison.LessUI);
        }

        public Operand ICompareNotEqual(Operand op1, Operand op2)
        {
            return ICompare(op1, op2, Comparison.NotEqual);
        }

        public Operand Load(OperandType type, Operand address)
        {
            return Add(Instruction.Load, Local(type), address);
        }

        public Operand Load16(Operand address)
        {
            return Add(Instruction.Load16, Local(OperandType.I32), address);
        }

        public Operand Load8(Operand address)
        {
            return Add(Instruction.Load8, Local(OperandType.I32), address);
        }

        public Operand LoadArgument(OperandType type, int index)
        {
            return Add(Instruction.LoadArgument, Local(type), Const(index));
        }

        public void LoadFromContext()
        {
            _needsNewBlock = true;

            Add(Instruction.LoadFromContext);
        }

        public Operand Multiply(Operand op1, Operand op2)
        {
            return Add(Instruction.Multiply, Local(op1.Type), op1, op2);
        }

        public Operand Multiply64HighSI(Operand op1, Operand op2)
        {
            return Add(Instruction.Multiply64HighSI, Local(OperandType.I64), op1, op2);
        }

        public Operand Multiply64HighUI(Operand op1, Operand op2)
        {
            return Add(Instruction.Multiply64HighUI, Local(OperandType.I64), op1, op2);
        }

        public Operand Negate(Operand op1)
        {
            return Add(Instruction.Negate, Local(op1.Type), op1);
        }

        public void Return()
        {
            Add(Instruction.Return);

            _needsNewBlock = true;
        }

        public void Return(Operand op1)
        {
            Add(Instruction.Return, null, op1);

            _needsNewBlock = true;
        }

        public Operand RotateRight(Operand op1, Operand op2)
        {
            return Add(Instruction.RotateRight, Local(op1.Type), op1, op2);
        }

        public Operand ShiftLeft(Operand op1, Operand op2)
        {
            return Add(Instruction.ShiftLeft, Local(op1.Type), op1, op2);
        }

        public Operand ShiftRightSI(Operand op1, Operand op2)
        {
            return Add(Instruction.ShiftRightSI, Local(op1.Type), op1, op2);
        }

        public Operand ShiftRightUI(Operand op1, Operand op2)
        {
            return Add(Instruction.ShiftRightUI, Local(op1.Type), op1, op2);
        }

        public Operand SignExtend16(OperandType type, Operand op1)
        {
            return Add(Instruction.SignExtend16, Local(type), op1);
        }

        public Operand SignExtend32(OperandType type, Operand op1)
        {
            return Add(Instruction.SignExtend32, Local(type), op1);
        }

        public Operand SignExtend8(OperandType type, Operand op1)
        {
            return Add(Instruction.SignExtend8, Local(type), op1);
        }

        public void Store(Operand address, Operand value)
        {
            Add(Instruction.Store, null, address, value);
        }

        public void Store16(Operand address, Operand value)
        {
            Add(Instruction.Store16, null, address, value);
        }

        public void Store8(Operand address, Operand value)
        {
            Add(Instruction.Store8, null, address, value);
        }

        public void StoreToContext()
        {
            Add(Instruction.StoreToContext);

            _needsNewBlock = true;
        }

        public Operand Subtract(Operand op1, Operand op2)
        {
            return Add(Instruction.Subtract, Local(op1.Type), op1, op2);
        }

        public Operand VectorCreateScalar(Operand value)
        {
            return Add(Instruction.VectorCreateScalar, Local(OperandType.V128), value);
        }

        public Operand VectorExtract(OperandType type, Operand vector, int index)
        {
            return Add(Instruction.VectorExtract, Local(type), vector, Const(index));
        }

        public Operand VectorExtract16(Operand vector, int index)
        {
            return Add(Instruction.VectorExtract16, Local(OperandType.I32), vector, Const(index));
        }

        public Operand VectorExtract8(Operand vector, int index)
        {
            return Add(Instruction.VectorExtract8, Local(OperandType.I32), vector, Const(index));
        }

        public Operand VectorInsert(Operand vector, Operand value, int index)
        {
            return Add(Instruction.VectorInsert, Local(OperandType.V128), vector, value, Const(index));
        }

        public Operand VectorInsert16(Operand vector, Operand value, int index)
        {
            return Add(Instruction.VectorInsert16, Local(OperandType.V128), vector, value, Const(index));
        }

        public Operand VectorInsert8(Operand vector, Operand value, int index)
        {
            return Add(Instruction.VectorInsert8, Local(OperandType.V128), vector, value, Const(index));
        }

        public Operand VectorOne()
        {
            return Add(Instruction.VectorOne, Local(OperandType.V128));
        }

        public Operand VectorZero()
        {
            return Add(Instruction.VectorZero, Local(OperandType.V128));
        }

        public Operand VectorZeroUpper64(Operand vector)
        {
            return Add(Instruction.VectorZeroUpper64, Local(OperandType.V128), vector);
        }

        public Operand VectorZeroUpper96(Operand vector)
        {
            return Add(Instruction.VectorZeroUpper96, Local(OperandType.V128), vector);
        }

        public Operand ZeroExtend16(OperandType type, Operand op1)
        {
            return Add(Instruction.ZeroExtend16, Local(type), op1);
        }

        public Operand ZeroExtend32(OperandType type, Operand op1)
        {
            return Add(Instruction.ZeroExtend32, Local(type), op1);
        }

        public Operand ZeroExtend8(OperandType type, Operand op1)
        {
            return Add(Instruction.ZeroExtend8, Local(type), op1);
        }

        private void NewNextBlockIfNeeded()
        {
            if (_needsNewBlock)
            {
                NewNextBlock();
            }
        }

        private Operand Add(Instruction inst, Operand dest = null)
        {
            NewNextBlockIfNeeded();

            Operation operation = OperationHelper.Operation(inst, dest);

            _irBlock.Operations.AddLast(operation);

            return dest;
        }

        private Operand Add(Instruction inst, Operand dest, Operand[] sources)
        {
            NewNextBlockIfNeeded();

            Operation operation = OperationHelper.Operation(inst, dest, sources);

            _irBlock.Operations.AddLast(operation);

            return dest;
        }

        private Operand Add(Instruction inst, Operand dest, Operand source0)
        {
            NewNextBlockIfNeeded();

            Operation operation = OperationHelper.Operation(inst, dest, source0);

            _irBlock.Operations.AddLast(operation);

            return dest;
        }

        private Operand Add(Instruction inst, Operand dest, Operand source0, Operand source1)
        {
            NewNextBlockIfNeeded();

            Operation operation = OperationHelper.Operation(inst, dest, source0, source1);

            _irBlock.Operations.AddLast(operation);

            return dest;
        }

        private Operand Add(Instruction inst, Operand dest, Operand source0, Operand source1, Operand source2)
        {
            NewNextBlockIfNeeded();

            Operation operation = OperationHelper.Operation(inst, dest, source0, source1, source2);

            _irBlock.Operations.AddLast(operation);

            return dest;
        }

        public Operand AddIntrinsic(Intrinsic intrin, params Operand[] args)
        {
            return Add(intrin, Local(OperandType.V128), args);
        }

        public Operand AddIntrinsicInt(Intrinsic intrin, params Operand[] args)
        {
            return Add(intrin, Local(OperandType.I32), args);
        }

        public Operand AddIntrinsicLong(Intrinsic intrin, params Operand[] args)
        {
            return Add(intrin, Local(OperandType.I64), args);
        }

        private Operand Add(Intrinsic intrin, Operand dest, params Operand[] sources)
        {
            if (_needsNewBlock)
            {
                NewNextBlock();
            }

            IntrinsicOperation operation = new IntrinsicOperation(intrin, dest, sources);

            _irBlock.Operations.AddLast(operation);

            return dest;
        }

        private void BranchToLabel(Operand label)
        {
            if (!_irLabels.TryGetValue(label, out BasicBlock branchBlock))
            {
                branchBlock = new BasicBlock();

                _irLabels.Add(label, branchBlock);
            }

            _irBlock.Branch = branchBlock;

            _needsNewBlock = true;
        }

        public void MarkLabel(Operand label)
        {
            if (_irLabels.TryGetValue(label, out BasicBlock nextBlock))
            {
                nextBlock.Index = _irBlocks.Count;

                _irBlocks.AddLast(nextBlock);

                NextBlock(nextBlock);
            }
            else
            {
                NewNextBlock();

                _irLabels.Add(label, _irBlock);
            }
        }

        private void NewNextBlock()
        {
            BasicBlock block = new BasicBlock(_irBlocks.Count);

            _irBlocks.AddLast(block);

            NextBlock(block);
        }

        private void NextBlock(BasicBlock nextBlock)
        {
            if (_irBlock != null && !EndsWithUnconditional(_irBlock))
            {
                _irBlock.Next = nextBlock;
            }

            _irBlock = nextBlock;

            _needsNewBlock = false;
        }

        private static bool EndsWithUnconditional(BasicBlock block)
        {
            return block.Operations.Last is Operation lastOp &&
                   (lastOp.Instruction == Instruction.Branch ||
                    lastOp.Instruction == Instruction.Return ||
                    lastOp.Instruction == Instruction.Tailcall);
        }

        public ControlFlowGraph GetControlFlowGraph()
        {
            return new ControlFlowGraph(_irBlocks.First, _irBlocks);
        }
    }
}
