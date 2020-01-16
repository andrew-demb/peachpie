﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Pchp.CodeAnalysis.Semantics;
using Pchp.CodeAnalysis.Semantics.Graph;
using Pchp.CodeAnalysis.Symbols;
using Peachpie.CodeAnalysis.Utilities;

namespace Pchp.CodeAnalysis.FlowAnalysis.Passes
{
    partial class TransformationRewriter
    {
        /// <summary>
        /// Each state in copy analysis maps each variable to a set of <see cref="BoundCopyValue"/> instances.
        /// Whenever a variable is modified, all the mapped copy operations are considered unavailable for removal
        /// due to the possible aliasing.
        /// </summary>
        private struct CopyAnalysisState
        {
            private BitMask[] _varState;

            public CopyAnalysisState(int varCount)
            {
                _varState = new BitMask[varCount];
            }

            public bool IsDefault => _varState == null;

            public int VariableCount => _varState.Length;

            public CopyAnalysisState Clone()
            {
                var clone = new CopyAnalysisState(_varState.Length);
                _varState.CopyTo(clone._varState, 0);

                return clone;
            }

            public bool Equals(CopyAnalysisState other)
            {
                if (this.IsDefault != other.IsDefault)
                    return false;

                if ((this.IsDefault && other.IsDefault) || _varState == other._varState)
                    return true;

                // We are supposed to compare only states from the same routine
                Debug.Assert(_varState.Length == other._varState.Length);

                for (int i = 0; i < other._varState.Length; i++)
                {
                    if (_varState[i] != other._varState[i])
                        return false;
                }

                return true;
            }

            public BitMask GetValue(int varIndex) => _varState[varIndex];

            public CopyAnalysisState WithMerge(CopyAnalysisState other)
            {
                if (this.IsDefault)
                    return other;
                else if (other.IsDefault)
                    return this;

                if (this.Equals(other))
                {
                    return this;
                }

                var merged = new CopyAnalysisState(_varState.Length);
                for (int i = 0; i < merged._varState.Length; i++)
                {
                    merged._varState[i] = _varState[i] | other._varState[i];
                }

                return merged;
            }

            public CopyAnalysisState WithValue(int varIndex, BitMask value)
            {
                Debug.Assert(!IsDefault);

                if (_varState[varIndex] == value)
                {
                    return this;
                }
                else
                {
                    var result = Clone();
                    result._varState[varIndex] = value;
                    return result;
                }
            }

            public CopyAnalysisState WithCopyAssignment(int trgVarIndex, int srcVarIndex, int copyIndex)
            {
                Debug.Assert(!IsDefault);

                var copyMask = BitMask.FromSingleValue(copyIndex);

                if (_varState[trgVarIndex] != copyMask || _varState[srcVarIndex] != (_varState[srcVarIndex] | copyMask))
                {
                    var result = Clone();
                    result._varState[trgVarIndex] = copyMask;
                    result._varState[srcVarIndex] |= copyMask;
                    return result;
                }
                else
                {
                    return this;
                }
            }
        }

        /// <summary>
        /// Implements copy analysis using <see cref="CopyAnalysisState"/>, producing a set of <see cref="BoundCopyValue"/>
        /// instances available for removal.
        /// </summary>
        private class CopyAnalysisContext : SingleBlockWalker<VoidStruct>, IFixPointAnalysisContext<CopyAnalysisState>
        {
            private readonly Dictionary<BoundCopyValue, int> _copyIndices = new Dictionary<BoundCopyValue, int>();
            private readonly FlowContext _flowContext;

            /// <summary>
            /// Set of <see cref="BoundCopyValue"/> instances located in return statements, to be filtered in the exit node.
            /// </summary>
            private HashSet<BoundCopyValue> _lazyReturnCopies;

            private CopyAnalysisState _state;
            private BitMask _neededCopies;

            private int VariableCount => _flowContext.VarsType.Length;

            private CopyAnalysisContext(FlowContext flowContext)
            {
                _flowContext = flowContext;
            }

            public static HashSet<BoundCopyValue> TryGetUnnecessaryCopies(SourceRoutineSymbol routine)
            {
                var cfg = routine.ControlFlowGraph;
                var context = new CopyAnalysisContext(cfg.FlowContext);
                var analysis = new FixPointAnalysis<CopyAnalysisContext, CopyAnalysisState>(context, routine);
                analysis.Run();

                HashSet<BoundCopyValue> result = context._lazyReturnCopies;  // context won't be used anymore, no need to copy the set
                foreach (var kvp in context._copyIndices)
                {
                    if (!context._neededCopies.Get(kvp.Value))
                    {
                        if (result == null)
                            result = new HashSet<BoundCopyValue>();

                        result.Add(kvp.Key);
                    }
                }

                return result;
            }

            public bool StatesEqual(CopyAnalysisState x, CopyAnalysisState y) => x.Equals(y);

            public CopyAnalysisState GetInitialState() => new CopyAnalysisState(VariableCount);

            public CopyAnalysisState MergeStates(CopyAnalysisState x, CopyAnalysisState y) => x.WithMerge(y);

            public CopyAnalysisState ProcessBlock(BoundBlock block, CopyAnalysisState state)
            {
                _state = state;
                block.Accept(this);
                return _state;
            }

            public override VoidStruct VisitAssign(BoundAssignEx assign)
            {
                ProcessAssignment(assign);
                return default;
            }

            private VariableHandle ProcessAssignment(BoundAssignEx assign)
            {
                bool CheckVariable(BoundVariableRef varRef, out VariableHandle handle)
                {
                    if (varRef.Name.IsDirect && !varRef.Name.NameValue.IsAutoGlobal
                        && !_flowContext.IsReference(handle = _flowContext.GetVarIndex(varRef.Name.NameValue)))
                    {
                        return true;
                    }
                    else
                    {
                        handle = default;
                        return false;
                    }
                }

                bool MatchSourceVarOrNestedAssignment(BoundExpression expr, out VariableHandle handle, out bool isCopied)
                {
                    if (MatchExprSkipCopy(expr, out BoundVariableRef varRef, out isCopied) && CheckVariable(varRef, out handle))
                    {
                        return true;
                    }
                    else if (MatchExprSkipCopy(expr, out BoundAssignEx nestedAssign, out isCopied))
                    {
                        handle = ProcessAssignment(nestedAssign);
                        return handle.IsValid;
                    }
                    else
                    {
                        handle = default;
                        return false;
                    }
                }

                // Handle assignment to a variable
                if (assign.Target is BoundVariableRef trgVarRef && CheckVariable(trgVarRef, out var trgHandle))
                {
                    if (MatchSourceVarOrNestedAssignment(assign.Value, out var srcHandle, out bool isCopied))
                    {
                        if (isCopied)
                        {
                            // Make the assignment a candidate for copy removal, possibly causing aliasing.
                            // It is removed if either trgVar or srcVar are modified later.
                            int copyIndex = EnsureCopyIndex((BoundCopyValue)assign.Value);
                            _state = _state.WithCopyAssignment(trgHandle, srcHandle, copyIndex);
                        }
                        else
                        {
                            // The copy was removed by a previous transformation, making them aliases sharing the assignments
                            _state = _state.WithValue(trgHandle, _state.GetValue(srcHandle));
                        }

                        // Visiting trgVar would destroy the effort (due to the assignment it's considered as MightChange),
                        // visiting srcVar is unnecessary
                        return trgHandle;
                    }
                    else
                    {
                        // Analyze the assigned expression
                        assign.Value.Accept(this);

                        // Do not attempt to remove copying from any other expression, just clear the assignment set for trgVar
                        _state = _state.WithValue(trgHandle, 0);

                        // Prevent from visiting trgVar due to its MightChange property
                        return trgHandle;
                    }
                }

                base.VisitAssign(assign);
                return default;
            }

            public override VoidStruct VisitVariableRef(BoundVariableRef x)
            {
                void MarkAllKnownAssignments()
                {
                    for (int i = 0; i < _state.VariableCount; i++)
                    {
                        _neededCopies |= _state.GetValue(i);
                    }
                }

                base.VisitVariableRef(x);

                // If a variable is modified, disable the deletion of all its current assignments
                if (x.Access.MightChange)
                {
                    if (!x.Name.IsDirect)
                    {
                        MarkAllKnownAssignments();
                    }
                    else if (!x.Name.NameValue.IsAutoGlobal)
                    {
                        var varindex = _flowContext.GetVarIndex(x.Name.NameValue);
                        if (!_flowContext.IsReference(varindex))
                        {
                            _neededCopies |= _state.GetValue(varindex);
                        }
                        else
                        {
                            // TODO: Mark only those that can be referenced
                            MarkAllKnownAssignments();
                        }
                    } 
                }

                return default;
            }

            public override VoidStruct VisitReturn(BoundReturnStatement x)
            {
                if (x.Returned is BoundCopyValue copy && copy.Expression is BoundVariableRef varRef &&
                    varRef.Name.IsDirect && !varRef.Name.NameValue.IsAutoGlobal)
                {
                    if (_lazyReturnCopies == null)
                        _lazyReturnCopies = new HashSet<BoundCopyValue>();

                    _lazyReturnCopies.Add(copy);
                }

                return base.VisitReturn(x);
            }

            public override VoidStruct VisitCFGExitBlock(ExitBlock x)
            {
                base.VisitCFGExitBlock(x);

                // Filter out the copies of variables in return statements which cannot be removed
                if (_lazyReturnCopies != null)
                {
                    List<BoundCopyValue> cannotRemove = null;

                    foreach (var returnCopy in _lazyReturnCopies)
                    {
                        var varRef = (BoundVariableRef)returnCopy.Expression;
                        var varindex = _flowContext.GetVarIndex(varRef.Name.NameValue);

                        // We cannot remove a variable which might alias any other variable due to
                        // a copying we removed earlier
                        if ((_state.GetValue(varindex) & ~_neededCopies) != 0)
                        {
                            if (cannotRemove == null)
                                cannotRemove = new List<BoundCopyValue>();

                            cannotRemove.Add(returnCopy);
                        }
                    }

                    if (cannotRemove != null)
                        _lazyReturnCopies.ExceptWith(cannotRemove);
                }

                return default;
            }

            private int EnsureCopyIndex(BoundCopyValue copy)
            {
                if (!_copyIndices.TryGetValue(copy, out int index))
                {
                    index = _copyIndices.Count;
                    _copyIndices.Add(copy, index);
                }

                return index;
            }
        }
    }
}
