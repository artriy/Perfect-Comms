using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace VoiceChatPlugin.VoiceChat;

// Vendored from Reactor (NuclearPowered/Reactor, LGPL) so we can patch IL2CPP
// compiler-generated coroutine state machines (e.g. InnerNetClient.HandleGameDataInner)
// the same way Reactor does, WITHOUT a Reactor dependency. Only used when Reactor is
// absent; when Reactor is loaded, VoiceJoinGuard stands down and Reactor drives this.
//
// ponytail: this is the one genuinely fragile piece (it reads Unity's mono-compiler
// field names "__4__this"/"__1__state"). It is wrapped in try/catch at every patch
// site so a future game/compiler change degrades to "guard disabled", never a crash.
internal class Il2CppCompilerGeneratedObjectWrapper
{
    public object GeneratedObject { get; }
    protected Type GeneratedType { get; }
    protected Dictionary<string, PropertyInfo> PropertyCache { get; }

    public Il2CppCompilerGeneratedObjectWrapper(object generatedObject)
    {
        GeneratedObject = generatedObject;
        GeneratedType = generatedObject.GetType();
        PropertyCache = new Dictionary<string, PropertyInfo>();
    }

    public PropertyInfo CacheProperty<T>(string fieldName)
    {
        var propertyInfo = AccessTools.Property(GeneratedType, fieldName)
            ?? throw new MissingMemberException($"Could not find field '{fieldName}' in type '{GeneratedType}'.");
        PropertyCache[fieldName] = propertyInfo;
        return propertyInfo;
    }

    public TField GetField<TField>(string fieldName)
    {
        if (!PropertyCache.TryGetValue(fieldName, out var propertyInfo))
            propertyInfo = CacheProperty<TField>(fieldName);
        return (TField) propertyInfo.GetValue(GeneratedObject)!;
    }

    public void SetField<TField>(string fieldName, TField value)
    {
        if (!PropertyCache.TryGetValue(fieldName, out var propertyInfo))
            propertyInfo = CacheProperty<TField>(fieldName);
        propertyInfo.SetValue(GeneratedObject, value);
    }
}

internal class Il2CppStateMachineWrapper<T> : Il2CppCompilerGeneratedObjectWrapper
{
    private readonly PropertyInfo _thisProperty;
    private readonly PropertyInfo _stateProperty;
    private T? _parentInstance;

    public T Instance => _parentInstance ??= (T) _thisProperty.GetValue(GeneratedObject)!;

    public int State
    {
        get => (int) _stateProperty.GetValue(GeneratedObject)!;
        set => _stateProperty.SetValue(GeneratedObject, value);
    }

    public Il2CppStateMachineWrapper(object stateMachine) : base(stateMachine)
    {
        _thisProperty = AccessTools.Property(GeneratedType, "__4__this");
        _stateProperty = AccessTools.Property(GeneratedType, "__1__state");
        if (_thisProperty == null || _stateProperty == null)
            throw new MissingMemberException($"Could not find required properties in type '{GeneratedType}'.");
    }

    public TField GetParameter<TField>(string parameterName) => GetField<TField>(parameterName);
    public void SetParameter<TField>(string parameterName, TField value) => SetField(parameterName, value);

    public static MethodBase? GetStateMachineMoveNext(string methodName)
    {
        var stateMachine = typeof(T).GetNestedTypes().FirstOrDefault(x => x.Name.Contains(methodName));
        if (stateMachine == null) return null;
        return AccessTools.Method(stateMachine, "MoveNext");
    }
}
