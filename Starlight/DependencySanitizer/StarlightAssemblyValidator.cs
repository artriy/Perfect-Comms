using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PerfectComms.DependencySanitizer;

internal static class StarlightAssemblyValidator
{
    private const string StarlightNativeLibraryName = "libstarlight.so";
    private static readonly IReadOnlySet<string> ApprovedCaptureImports =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "starlight_voice_has_record_audio_permission",
            "starlight_voice_request_record_audio_permission",
            "starlight_voice_start_capture",
            "starlight_voice_read_float",
            "starlight_voice_stop_capture",
            "starlight_voice_is_capture_running",
            "starlight_voice_get_sample_rate",
            "starlight_voice_get_buffer_frames",
            "starlight_voice_get_last_error"
        };
    internal static bool IsApprovedCaptureImport(MethodDefinition method)
    {
        var import = method.PInvokeInfo;
        if (import is null ||
            !method.IsPInvokeImpl ||
            !method.IsStatic ||
            method.HasThis ||
            method.HasBody ||
            method.HasGenericParameters ||
            !string.Equals(import.Module.Name, StarlightNativeLibraryName, StringComparison.Ordinal) ||
            import.Attributes != PInvokeAttributes.CallConvCdecl ||
            method.ImplAttributes != MethodImplAttributes.PreserveSig)
        {
            return false;
        }

        var entryPoint = string.IsNullOrEmpty(import.EntryPoint) ? method.Name : import.EntryPoint;
        if (!ApprovedCaptureImports.Contains(entryPoint))
        {
            return false;
        }

        return entryPoint switch
        {
            "starlight_voice_has_record_audio_permission" =>
                IsBooleanI1Return(method) && HasPlainParameters(method),
            "starlight_voice_request_record_audio_permission" =>
                IsBooleanI1Return(method) && HasPlainParameters(method),
            "starlight_voice_start_capture" =>
                HasReturnType(method, "System.Int32") &&
                HasPlainParameters(method, "System.Int32", "System.Int32"),
            "starlight_voice_read_float" =>
                HasReturnType(method, "System.Int32") &&
                HasParameterTypes(method, "System.Single[]", "System.Int32") &&
                method.Parameters[0].Attributes == ParameterAttributes.Out &&
                !method.Parameters[0].HasMarshalInfo &&
                method.Parameters[1].Attributes == ParameterAttributes.None &&
                !method.Parameters[1].HasMarshalInfo,
            "starlight_voice_stop_capture" =>
                HasReturnType(method, "System.Void") && HasPlainParameters(method),
            "starlight_voice_is_capture_running" =>
                IsBooleanI1Return(method) && HasPlainParameters(method),
            "starlight_voice_get_sample_rate" =>
                HasReturnType(method, "System.Int32") && HasPlainParameters(method),
            "starlight_voice_get_buffer_frames" =>
                HasReturnType(method, "System.Int32") && HasPlainParameters(method),
            "starlight_voice_get_last_error" =>
                HasReturnType(method, "System.IntPtr") && HasPlainParameters(method),
            _ => false
        };
    }

    private static bool HasReturnType(MethodDefinition method, string expectedType)
    {
        return string.Equals(method.ReturnType.FullName, expectedType, StringComparison.Ordinal) &&
               !method.MethodReturnType.HasMarshalInfo;
    }

    private static bool IsBooleanI1Return(MethodDefinition method)
    {
        return string.Equals(method.ReturnType.FullName, "System.Boolean", StringComparison.Ordinal) &&
               method.MethodReturnType.MarshalInfo?.NativeType == NativeType.I1;
    }

    private static bool HasPlainParameters(MethodDefinition method, params string[] expectedTypes)
    {
        return HasParameterTypes(method, expectedTypes) &&
               method.Parameters.All(static parameter =>
                   parameter.Attributes == ParameterAttributes.None &&
                   !parameter.HasMarshalInfo);
    }

    private static bool HasParameterTypes(MethodDefinition method, params string[] expectedTypes)
    {
        return method.Parameters.Count == expectedTypes.Length &&
               method.Parameters
                   .Select(static parameter => parameter.ParameterType.FullName)
                   .SequenceEqual(expectedTypes, StringComparer.Ordinal);
    }


    internal static bool HasExactApprovedCaptureImports(ModuleDefinition module)
    {
        var imports = EnumerateTypes(module.Types)
            .SelectMany(static type => type.Methods)
            .Where(static method => method.IsPInvokeImpl || method.PInvokeInfo is not null)
            .ToArray();
        return imports.Length == ApprovedCaptureImports.Count &&
               imports.All(IsApprovedCaptureImport) &&
               imports
                   .Select(static method =>
                       string.IsNullOrEmpty(method.PInvokeInfo!.EntryPoint)
                           ? method.Name
                           : method.PInvokeInfo.EntryPoint)
                   .ToHashSet(StringComparer.Ordinal)
                   .SetEquals(ApprovedCaptureImports);
    }

    internal static bool IsAssemblySafe(string assemblyPath)
    {
        try
        {
            using var module = ModuleDefinition.ReadModule(assemblyPath);
            foreach (var type in EnumerateTypes(module.Types))
            {
                if (type.Fields.Any(static field => ContainsFunctionPointer(field.FieldType)) ||
                    type.Properties.Any(static property =>
                        ContainsFunctionPointer(property.PropertyType) ||
                        property.Parameters.Any(static parameter =>
                            ContainsFunctionPointer(parameter.ParameterType))) ||
                    type.Events.Any(static @event => ContainsFunctionPointer(@event.EventType)))
                {
                    return false;
                }

                foreach (var method in type.Methods)
                {
                    var hasPInvokeAttribute = (method.Attributes & MethodAttributes.PInvokeImpl) != 0;
                    var hasPInvokeInfo = method.PInvokeInfo is not null;
                    if (hasPInvokeAttribute != hasPInvokeInfo)
                    {
                        return false;
                    }

                    if (method.PInvokeInfo is not null &&
                        !IsApprovedCaptureImport(method))
                    {
                        return false;
                    }

                    if (ContainsFunctionPointer(method.ReturnType) ||
                        method.Parameters.Any(static parameter =>
                            ContainsFunctionPointer(parameter.ParameterType)))
                    {
                        return false;
                    }

                    if (!method.HasBody)
                    {
                        continue;
                    }

                    if (method.Body.Variables.Any(static variable =>
                            ContainsFunctionPointer(variable.VariableType)))
                    {
                        return false;
                    }

                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode == OpCodes.Calli)
                        {
                            return false;
                        }

                        if (instruction.Operand is not MethodReference methodReference)
                        {
                            continue;
                        }

                        if (string.Equals(
                                methodReference.DeclaringType.FullName,
                                "System.Runtime.InteropServices.NativeLibrary",
                                StringComparison.Ordinal))
                        {
                            return false;
                        }

                        if (string.Equals(
                                methodReference.DeclaringType.FullName,
                                "System.Runtime.InteropServices.Marshal",
                                StringComparison.Ordinal) &&
                            methodReference.Name is "GetDelegateForFunctionPointer" or
                                "GetFunctionPointerForDelegate")
                        {
                            return false;
                        }
                    }
                }
            }

            return HasExactApprovedCaptureImports(module);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void ValidateStrictPe(Stream assemblyStream, string assemblyPath)
    {
        using var peReader = new PEReader(assemblyStream, PEStreamOptions.LeaveOpen);
        var corHeader = peReader.PEHeaders.CorHeader;
        if (corHeader is null)
        {
            throw new InvalidOperationException($"CLI header is missing from '{assemblyPath}'.");
        }

        if ((corHeader.Flags & CorFlags.ILOnly) == 0)
        {
            throw new InvalidOperationException($"CorFlags.ILOnly is not set on '{assemblyPath}'.");
        }

        if ((corHeader.Flags & CorFlags.NativeEntryPoint) != 0)
        {
            throw new InvalidOperationException($"CorFlags.NativeEntryPoint is set on '{assemblyPath}'.");
        }

        var managedNativeHeader = corHeader.ManagedNativeHeaderDirectory;
        if (managedNativeHeader.RelativeVirtualAddress != 0 || managedNativeHeader.Size != 0)
        {
            throw new InvalidOperationException($"Managed native header is present on '{assemblyPath}'.");
        }
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nestedType in EnumerateTypes(type.NestedTypes))
            {
                yield return nestedType;
            }
        }
    }
    private static bool ContainsFunctionPointer(TypeReference type)
    {
        if (type is FunctionPointerType)
        {
            return true;
        }

        if (type is GenericInstanceType generic &&
            generic.GenericArguments.Any(ContainsFunctionPointer))
        {
            return true;
        }

        return type is TypeSpecification specification &&
               ContainsFunctionPointer(specification.ElementType);
    }


}
