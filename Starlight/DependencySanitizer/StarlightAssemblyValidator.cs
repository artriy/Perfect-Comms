using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PerfectComms.DependencySanitizer;

internal static class StarlightAssemblyValidator
{
    internal static bool IsAssemblySafe(string assemblyPath)
    {
        var whitelistedDllImports = new Dictionary<string, List<string>>
        {
            ["user32.dll"] =
            [
                "GetForegroundWindow", "MessageBox"
            ],
            ["winmm.dll"] = ["*"],
            ["libstarlight.so"] =
            [
                "get_width",
                "get_height",
                "create_alert",
                "open_url",
                "get_string",
                "get_lobby",
                "quit_app",
                "starlight_voice_has_record_audio_permission",
                "starlight_voice_request_record_audio_permission",
                "starlight_voice_start_capture",
                "starlight_voice_read_float",
                "starlight_voice_stop_capture",
                "starlight_voice_is_capture_running",
                "starlight_voice_get_sample_rate",
                "starlight_voice_get_buffer_frames",
                "starlight_voice_get_last_error"
            ]
        };

        try
        {
            using var module = ModuleDefinition.ReadModule(assemblyPath);
            foreach (var type in EnumerateTypes(module.Types))
            {
                foreach (var method in type.Methods)
                {
                    if (method.IsPInvokeImpl)
                    {
                        var dllName = NormalizeNativeLibraryName(method.PInvokeInfo.Module.Name);
                        if (!whitelistedDllImports.TryGetValue(dllName, out var whitelistedMethods))
                        {
                            return false;
                        }

                        var entryPoint = method.PInvokeInfo.EntryPoint;
                        if (string.IsNullOrEmpty(entryPoint))
                        {
                            entryPoint = method.Name;
                        }

                        if (!whitelistedMethods.Contains("*") && !whitelistedMethods.Contains(entryPoint))
                        {
                            return false;
                        }
                    }

                    if (!method.HasBody)
                    {
                        continue;
                    }

                    foreach (var instruction in method.Body.Instructions)
                    {
                        if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
                            instruction.Operand is MethodReference methodReference &&
                            methodReference.DeclaringType.FullName ==
                            "System.Runtime.InteropServices.NativeLibrary")
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
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

    private static string NormalizeNativeLibraryName(string libraryName)
    {
        var normalized = libraryName.ToLowerInvariant();
        return normalized is "starlight" or "starlight.so" or "libstarlight"
            ? "libstarlight.so"
            : normalized;
    }
}
