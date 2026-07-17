using System.Linq.Expressions;
using System.Reflection;
using PerfectComms.Api;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class PerfectCommsIntegrationReflectionAbiTests
{
    // Compatibility fixture derived from the unchanged bridge at blob
    // 57896484ab21b950a621a2db99081ba83daa7f76 in HekerB/TownOfUsMegaChujoweExtension.
    private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
    private const string ExpectedPluginId = "com.edgetel.perfectcomms";

    private static int _listenerFilterCalls;

    [Fact]
    public void UnchangedBridgeFindsRequiredTypesMembersEnumsAndConstructors()
    {
        Assembly assembly = typeof(PerfectCommsApi).Assembly;
        Assert.Equal("PerfectComms", assembly.GetName().Name);

        Type apiType = RequiredType(assembly, "PerfectComms.Api.PerfectCommsApi");
        Type ruleContextType = RequiredType(assembly, "PerfectComms.Api.VoiceRuleContext");
        Type ruleResultType = RequiredType(assembly, "PerfectComms.Api.VoiceRuleResult");
        Type phaseType = RequiredType(assembly, "PerfectComms.Api.VoicePhaseKind");
        Type channelResultType = RequiredType(assembly, "PerfectComms.Api.VoiceChannelResult");
        Type audioShapeType = RequiredType(assembly, "PerfectComms.Api.VoiceAudioShape");
        Type hostOptionType = RequiredType(assembly, "PerfectComms.Api.VoiceHostOption");
        Type hostEnumOptionType = RequiredType(assembly, "PerfectComms.Api.VoiceHostEnumOption");

        FieldInfo pluginId = AssertField(apiType, "PluginId", typeof(string));
        Assert.Equal(ExpectedPluginId, pluginId.GetRawConstantValue());

        AssertProperty(ruleContextType, "Player", typeof(PlayerControl));
        AssertProperty(ruleContextType, "Phase", phaseType);
        AssertProperty(ruleContextType, "IsDead", typeof(bool));
        AssertProperty(ruleContextType, "GetOption", typeof(Func<string, bool>));
        AssertProperty(ruleContextType, "GetEnumOption", typeof(Func<string, int>));

        Assert.True(phaseType.IsEnum);
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(phaseType));
        AssertEnumValue(phaseType, "Lobby", 0);
        AssertEnumValue(phaseType, "Tasks", 1);
        AssertEnumValue(phaseType, "Meeting", 2);
        AssertEnumValue(phaseType, "Exile", 3);

        Assert.True(audioShapeType.IsEnum);
        object radioShape = Enum.Parse(audioShapeType, "Radio");
        Assert.Equal("Radio", Enum.GetName(audioShapeType, radioShape));

        FieldInfo pass = AssertField(ruleResultType, "Pass", ruleResultType);
        object passResult = AssertRuntimeType(ruleResultType, pass.GetValue(null));

        MethodInfo mute = AssertUniqueNameOnlyMethod(
            ruleResultType,
            "Mute",
            ruleResultType,
            typeof(string));
        object muteResult = AssertRuntimeType(ruleResultType, mute.Invoke(null, new object[] { "Legacy mute" }));
        Assert.Equal("Legacy mute", ruleResultType.GetProperty("Reason")!.GetValue(muteResult));

        Assert.NotNull(hostOptionType.GetConstructor(new[]
        {
            typeof(string), typeof(string), typeof(bool),
        }));
        object hostOption = AssertRuntimeType(
            hostOptionType,
            Activator.CreateInstance(hostOptionType, "legacy-toggle", "Legacy toggle", true));
        Assert.Equal("legacy-toggle", hostOptionType.GetProperty("Key")!.GetValue(hostOption));
        Assert.Equal(true, hostOptionType.GetProperty("Default")!.GetValue(hostOption));

        Assert.NotNull(hostEnumOptionType.GetConstructor(new[]
        {
            typeof(string), typeof(string), typeof(int), typeof(string[]),
        }));
        string[] choices = { "Off", "Both Ways" };
        object hostEnumOption = AssertRuntimeType(
            hostEnumOptionType,
            Activator.CreateInstance(
                hostEnumOptionType,
                "legacy-enum",
                "Legacy enum",
                1,
                choices));
        Assert.Equal(1, hostEnumOptionType.GetProperty("Default")!.GetValue(hostEnumOption));
        Assert.Same(choices, hostEnumOptionType.GetProperty("Choices")!.GetValue(hostEnumOption));

        Assert.NotNull(channelResultType.GetConstructor(new[]
        {
            typeof(string), typeof(bool), audioShapeType, typeof(float), typeof(Vector2?),
        }));
        object channelResult = AssertRuntimeType(
            channelResultType,
            Activator.CreateInstance(
                channelResultType,
                "legacy-radio",
                true,
                radioShape,
                1f,
                null));
        Assert.Equal("legacy-radio", channelResultType.GetProperty("Key")!.GetValue(channelResult));
        Assert.Equal(true, channelResultType.GetProperty("TwoWay")!.GetValue(channelResult));
        Assert.Equal(radioShape, channelResultType.GetProperty("Shape")!.GetValue(channelResult));
        Assert.Equal(1f, channelResultType.GetProperty("Volume")!.GetValue(channelResult));
        Assert.Null(channelResultType.GetProperty("Origin")!.GetValue(channelResult));

        Assert.Same(passResult, pass.GetValue(null));
    }

    [Fact]
    public void UnchangedBridgeNameOnlyLookupFindsOneExactLegacyApiMethodPerName()
    {
        Assembly assembly = typeof(PerfectCommsApi).Assembly;
        Type apiType = RequiredType(assembly, "PerfectComms.Api.PerfectCommsApi");
        Type ruleContextType = RequiredType(assembly, "PerfectComms.Api.VoiceRuleContext");
        Type ruleResultType = RequiredType(assembly, "PerfectComms.Api.VoiceRuleResult");
        Type phaseType = RequiredType(assembly, "PerfectComms.Api.VoicePhaseKind");
        Type channelResultType = RequiredType(assembly, "PerfectComms.Api.VoiceChannelResult");
        Type hostOptionType = RequiredType(assembly, "PerfectComms.Api.VoiceHostOption");
        Type hostEnumOptionType = RequiredType(assembly, "PerfectComms.Api.VoiceHostEnumOption");

        Type ruleDelegateType = typeof(Func<,>).MakeGenericType(ruleContextType, ruleResultType);
        Type channelDelegateType = typeof(Func<,>).MakeGenericType(ruleContextType, channelResultType);
        Type listenerFilterDelegateType = typeof(Func<,>).MakeGenericType(typeof(PlayerControl), typeof(bool));

        AssertUniqueNameOnlyMethod(apiType, "RegisterModTab", typeof(void), typeof(string), typeof(string));
        AssertUniqueNameOnlyMethod(apiType, "RegisterHostOption", typeof(void), typeof(string), hostOptionType);
        AssertUniqueNameOnlyMethod(apiType, "RegisterHostEnumOption", typeof(void), typeof(string), hostEnumOptionType);
        AssertUniqueNameOnlyMethod(apiType, "RegisterVoiceRule", typeof(void), typeof(string), ruleDelegateType);
        AssertUniqueNameOnlyMethod(
            apiType,
            "RegisterGlobalGate",
            typeof(void),
            typeof(string),
            phaseType,
            typeof(Func<bool>),
            typeof(string));
        AssertUniqueNameOnlyMethod(apiType, "RegisterVoiceChannel", typeof(void), typeof(string), channelDelegateType);
        AssertUniqueNameOnlyMethod(
            apiType,
            "RegisterListenerFilter",
            typeof(void),
            typeof(string),
            listenerFilterDelegateType);
        AssertUniqueNameOnlyMethod(apiType, "Unregister", typeof(void), typeof(string));
    }

    [Fact]
    public void UnchangedBridgeCanConstructDelegatesRegisterLegacySurfaceAndUnregister()
    {
        Assembly assembly = typeof(PerfectCommsApi).Assembly;
        Type apiType = RequiredType(assembly, "PerfectComms.Api.PerfectCommsApi");
        Type ruleContextType = RequiredType(assembly, "PerfectComms.Api.VoiceRuleContext");
        Type ruleResultType = RequiredType(assembly, "PerfectComms.Api.VoiceRuleResult");
        Type phaseType = RequiredType(assembly, "PerfectComms.Api.VoicePhaseKind");
        Type channelResultType = RequiredType(assembly, "PerfectComms.Api.VoiceChannelResult");
        Type audioShapeType = RequiredType(assembly, "PerfectComms.Api.VoiceAudioShape");
        Type hostOptionType = RequiredType(assembly, "PerfectComms.Api.VoiceHostOption");
        Type hostEnumOptionType = RequiredType(assembly, "PerfectComms.Api.VoiceHostEnumOption");

        object passResult = ruleResultType.GetField("Pass", PublicStatic)!.GetValue(null)!;
        object radioShape = Enum.Parse(audioShapeType, "Radio");
        object channelResult = Activator.CreateInstance(
            channelResultType,
            "legacy-radio",
            true,
            radioShape,
            1f,
            null)!;
        object hostOption = Activator.CreateInstance(
            hostOptionType,
            "legacy-toggle",
            "Legacy toggle",
            true)!;
        object hostEnumOption = Activator.CreateInstance(
            hostEnumOptionType,
            "legacy-enum",
            "Legacy enum",
            1,
            new[] { "Off", "Both Ways" })!;

        int ruleCalls = 0;
        int channelCalls = 0;
        int globalGateCalls = 0;
        Func<object, object> rule = _ =>
        {
            ruleCalls++;
            return passResult;
        };
        Func<object, object?> channel = _ =>
        {
            channelCalls++;
            return channelResult;
        };
        Func<bool> globalGate = () =>
        {
            globalGateCalls++;
            return false;
        };

        Type ruleDelegateType = typeof(Func<,>).MakeGenericType(ruleContextType, ruleResultType);
        Delegate ruleCallback = BuildObjectCallback(
            ruleDelegateType,
            ruleContextType,
            ruleResultType,
            rule);
        Type channelDelegateType = typeof(Func<,>).MakeGenericType(ruleContextType, channelResultType);
        Delegate channelCallback = BuildObjectCallback(
            channelDelegateType,
            ruleContextType,
            channelResultType,
            channel);

        _listenerFilterCalls = 0;
        Func<PlayerControl, bool> shouldMuffle = LegacyListenerFilter;
        Type listenerFilterDelegateType = typeof(Func<,>).MakeGenericType(typeof(PlayerControl), typeof(bool));
        Delegate listenerFilterCallback = Delegate.CreateDelegate(
            listenerFilterDelegateType,
            shouldMuffle.Target,
            shouldMuffle.Method);

        object callbackContext = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(ruleContextType);
        Assert.Same(passResult, ruleCallback.DynamicInvoke(callbackContext));
        Assert.Same(channelResult, channelCallback.DynamicInvoke(callbackContext));
        object fakePlayer = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerControl));
        Assert.Equal(true, listenerFilterCallback.DynamicInvoke(fakePlayer));
        Assert.Equal(1, ruleCalls);
        Assert.Equal(1, channelCalls);
        Assert.Equal(1, _listenerFilterCalls);

        string modId = "tests.toumce.reflection." + Guid.NewGuid().ToString("N");
        InvokeNameOnly(apiType, "Unregister", modId);

        try
        {
            InvokeNameOnly(apiType, "RegisterModTab", modId, "ToU: Chujowe");
            InvokeNameOnly(apiType, "RegisterHostOption", modId, hostOption);
            InvokeNameOnly(apiType, "RegisterHostEnumOption", modId, hostEnumOption);
            InvokeNameOnly(apiType, "RegisterVoiceRule", modId, ruleCallback);
            InvokeNameOnly(
                apiType,
                "RegisterGlobalGate",
                modId,
                Enum.ToObject(phaseType, 1),
                globalGate,
                "Hacker Jam");
            InvokeNameOnly(apiType, "RegisterListenerFilter", modId, listenerFilterCallback);
            InvokeNameOnly(apiType, "RegisterVoiceChannel", modId, channelCallback);

            Assert.Contains(VoiceModRegistry.Tabs, tab => tab.ModId == modId && tab.Label == "ToU: Chujowe");
            VoiceHostOption registeredBool = Assert.Single(VoiceModRegistry.BoolOptionsFor(modId));
            Assert.Equal("legacy-toggle", registeredBool.Key);
            VoiceHostEnumOption registeredEnum = Assert.Single(VoiceModRegistry.EnumOptionsFor(modId));
            Assert.Equal("legacy-enum", registeredEnum.Key);
            Assert.True(VoiceModRegistry.HasAnyRegistrations);

            Assert.False(globalGate());
            Assert.Equal(1, globalGateCalls);

            InvokeNameOnly(apiType, "Unregister", modId);

            Assert.DoesNotContain(VoiceModRegistry.Tabs, tab => tab.ModId == modId);
            Assert.Empty(VoiceModRegistry.BoolOptionsFor(modId));
            Assert.Empty(VoiceModRegistry.EnumOptionsFor(modId));
            Assert.False(VoiceModRegistry.HasAnyRegistrations);
        }
        finally
        {
            InvokeNameOnly(apiType, "Unregister", modId);
        }
    }

    private static Type RequiredType(Assembly assembly, string typeName)
        => assembly.GetType(typeName)
           ?? throw new TypeLoadException($"Perfect Comms API type not found: {typeName}");

    private static FieldInfo AssertField(Type declaringType, string fieldName, Type fieldType)
    {
        FieldInfo field = declaringType.GetField(fieldName, PublicStatic)
                          ?? throw new MissingMemberException(declaringType.FullName, fieldName);
        Assert.Equal(fieldType, field.FieldType);
        return field;
    }

    private static object AssertRuntimeType(Type expectedType, object? value)
    {
        Assert.NotNull(value);
        Assert.Equal(expectedType, value!.GetType());
        return value;
    }

    private static void AssertProperty(Type declaringType, string propertyName, Type propertyType)
    {
        PropertyInfo property = declaringType.GetProperty(propertyName, PublicInstance)
                                ?? throw new MissingMemberException(declaringType.FullName, propertyName);
        Assert.Equal(propertyType, property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.True(property.GetMethod!.IsPublic);
    }

    private static void AssertEnumValue(Type enumType, string name, int expectedValue)
    {
        object value = Enum.Parse(enumType, name);
        Assert.Equal(expectedValue, Convert.ToInt32(value));
        Assert.Equal(name, Enum.GetName(enumType, value));
    }

    private static MethodInfo AssertUniqueNameOnlyMethod(
        Type declaringType,
        string methodName,
        Type returnType,
        params Type[] parameterTypes)
    {
        MethodInfo[] sameName = declaringType
            .GetMethods(PublicStatic)
            .Where(method => method.Name == methodName)
            .ToArray();
        Assert.Single(sameName);

        // This is deliberately name-only: it mirrors the unchanged external bridge and will throw
        // AmbiguousMatchException if a future additive API accidentally reuses a legacy name.
        MethodInfo method = declaringType.GetMethod(methodName, PublicStatic)
                            ?? throw new MissingMethodException(declaringType.FullName, methodName);
        Assert.Equal(returnType, method.ReturnType);
        Assert.Equal(parameterTypes, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        return method;
    }

    private static Delegate BuildObjectCallback(
        Type delegateType,
        Type parameterType,
        Type returnType,
        Delegate callback)
    {
        ParameterExpression parameter = Expression.Parameter(parameterType, "context");
        InvocationExpression invoke = Expression.Invoke(
            Expression.Constant(callback),
            Expression.Convert(parameter, typeof(object)));
        UnaryExpression body = Expression.Convert(invoke, returnType);
        return Expression.Lambda(delegateType, body, parameter).Compile();
    }

    private static void InvokeNameOnly(Type apiType, string methodName, params object[] args)
    {
        MethodInfo method = apiType.GetMethod(methodName, PublicStatic)
                            ?? throw new MissingMethodException(apiType.FullName, methodName);
        method.Invoke(null, args);
    }

    private static bool LegacyListenerFilter(PlayerControl _)
    {
        _listenerFilterCalls++;
        return true;
    }
}
