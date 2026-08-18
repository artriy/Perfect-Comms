using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace VoiceChatPlugin.VoiceChat;

internal static class PlayerVoteAreaPlayerId
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Func<object, byte>? Reader;
    private static readonly string? ResolvedMemberName;
    private static bool _failureLogged;

    static PlayerVoteAreaPlayerId()
    {
        Reader = CreateReader(typeof(PlayerVoteArea), out ResolvedMemberName);
    }

    internal static bool TryRead(PlayerVoteArea? voteArea, out byte playerId)
    {
        playerId = byte.MaxValue;
        if (voteArea == null) return false;

        var reader = Reader;
        if (reader == null)
        {
            WarnOnce("neither PlayerId nor TargetPlayerId is readable");
            return false;
        }

        try
        {
            playerId = reader(voteArea);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce($"{ResolvedMemberName ?? "unknown"} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static Func<object, byte>? CreateReader(Type voteAreaType, out string? resolvedMemberName)
    {
        ArgumentNullException.ThrowIfNull(voteAreaType);

        foreach (var memberName in new[] { "PlayerId", "TargetPlayerId" })
        {
            var member = FindReadableMember(voteAreaType, memberName);
            if (member == null || !CanNormalizeToByte(GetMemberType(member))) continue;

            resolvedMemberName = memberName;
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                try
                {
                    return EmitReader(voteAreaType, member);
                }
                catch (Exception)
                {
                }
            }

            return CreateReflectionReader(member);
        }

        resolvedMemberName = null;
        return null;
    }

    private static MemberInfo? FindReadableMember(Type type, string name)
    {
        var property = type.GetProperty(name, InstanceMembers);
        if (property?.GetGetMethod(true) != null) return property;
        return type.GetField(name, InstanceMembers);
    }

    private static Type GetMemberType(MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new ArgumentOutOfRangeException(nameof(member)),
        };

    private static bool CanNormalizeToByte(Type type)
        => type == typeof(byte)
           || FindByteConversion(type) != null
           || FindByteValueProperty(type) != null
           || FindByteValueField(type) != null;

    private static MethodInfo? FindByteConversion(Type type)
    {
        foreach (var method in type.GetMethods(StaticMembers))
        {
            if (method.Name is not ("op_Implicit" or "op_Explicit") || method.ReturnType != typeof(byte))
                continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == type)
                return method;
        }

        return null;
    }

    private static PropertyInfo? FindByteValueProperty(Type type)
    {
        var property = type.GetProperty("Value", InstanceMembers);
        return property?.PropertyType == typeof(byte) && property.GetGetMethod(true) != null ? property : null;
    }

    private static FieldInfo? FindByteValueField(Type type)
    {
        var field = type.GetField("Value", InstanceMembers);
        return field?.FieldType == typeof(byte) ? field : null;
    }

    private static Func<object, byte> EmitReader(Type voteAreaType, MemberInfo member)
    {
        var dynamicMethod = new DynamicMethod(
            $"PerfectComms_Read_{voteAreaType.Name}_{member.Name}",
            typeof(byte),
            new[] { typeof(object) },
            typeof(PlayerVoteAreaPlayerId).Module,
            true);
        var il = dynamicMethod.GetILGenerator();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, voteAreaType);
        var valueType = EmitMemberRead(il, member);
        EmitByteNormalization(il, valueType);
        il.Emit(OpCodes.Ret);

        return (Func<object, byte>)dynamicMethod.CreateDelegate(typeof(Func<object, byte>));
    }

    private static Type EmitMemberRead(ILGenerator il, MemberInfo member)
    {
        switch (member)
        {
            case PropertyInfo property:
                il.Emit(OpCodes.Callvirt, property.GetGetMethod(true)!);
                return property.PropertyType;
            case FieldInfo field:
                il.Emit(OpCodes.Ldfld, field);
                return field.FieldType;
            default:
                throw new ArgumentOutOfRangeException(nameof(member));
        }
    }

    private static void EmitByteNormalization(ILGenerator il, Type valueType)
    {
        if (valueType == typeof(byte)) return;

        var conversion = FindByteConversion(valueType);
        if (conversion != null)
        {
            il.Emit(OpCodes.Call, conversion);
            return;
        }

        LocalBuilder? value = null;
        if (valueType.IsValueType)
        {
            value = il.DeclareLocal(valueType);
            il.Emit(OpCodes.Stloc, value);
            il.Emit(OpCodes.Ldloca, value);
        }

        var valueProperty = FindByteValueProperty(valueType);
        if (valueProperty != null)
        {
            il.Emit(valueType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, valueProperty.GetGetMethod(true)!);
            return;
        }

        var valueField = FindByteValueField(valueType)
                         ?? throw new MissingMemberException(valueType.FullName, "Value");
        il.Emit(OpCodes.Ldfld, valueField);
    }

    private static Func<object, byte> CreateReflectionReader(MemberInfo member)
    {
        var normalize = CreateReflectionNormalizer(GetMemberType(member));
        return member switch
        {
            PropertyInfo property => instance => normalize(property.GetValue(instance)),
            FieldInfo field => instance => normalize(field.GetValue(instance)),
            _ => throw new ArgumentOutOfRangeException(nameof(member)),
        };
    }

    private static Func<object?, byte> CreateReflectionNormalizer(Type valueType)
    {
        if (valueType == typeof(byte))
            return value => value is byte playerId ? playerId : throw new InvalidCastException();

        var conversion = FindByteConversion(valueType);
        if (conversion != null)
            return value => (byte)(conversion.Invoke(null, new[] { value }) ?? throw new InvalidCastException());

        var valueProperty = FindByteValueProperty(valueType);
        if (valueProperty != null)
            return value => (byte)(valueProperty.GetValue(value) ?? throw new InvalidCastException());

        var valueField = FindByteValueField(valueType)
                         ?? throw new MissingMemberException(valueType.FullName, "Value");
        return value => (byte)(valueField.GetValue(value) ?? throw new InvalidCastException());
    }

    private static void WarnOnce(string reason)
    {
        if (_failureLogged) return;
        _failureLogged = true;
        VoiceDiagnostics.DebugWarning($"[VC] Meeting player ID compatibility access failed ({reason}); affected meeting card is ignored.");
    }
}
