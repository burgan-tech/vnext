using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using BBT.Workflow.Scripting.Functions;

namespace BBT.Workflow.Scripting.Evaluators;

/// <summary>
/// Allocation-light activator for compiled script types: a compiled parameterless-ctor delegate is
/// built once per <see cref="Type"/> and reused (replaces per-call <see cref="Activator.CreateInstance(Type)"/>).
/// Service injection semantics are identical to the evaluator's historical behaviour: a fresh
/// instance per call, <see cref="ScriptBase.SetServices"/> when applicable.
/// </summary>
public static class ScriptActivator
{
    private static readonly ConcurrentDictionary<Type, Func<object>> Factories = new();

    public static T Create<T>(Type compiledType, IScriptServices? services)
    {
        var factory = Factories.GetOrAdd(compiledType, static t =>
            Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(t), typeof(object))).Compile());

        var instance = (T)factory();
        if (instance is ScriptBase scriptBase && services != null)
        {
            scriptBase.SetServices(services);
        }
        return instance;
    }
}
