using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ILRepacking;

namespace PerfectComms.DependencySanitizer;

internal static class Program
{
    private const string MediaAssemblyName = "PerfectComms.Starlight.Media";
    private const string PluginAssemblyName = "PerfectComms";
    private const string OutputFileName = "PerfectCommsStarlight.dll";
    private const string RequiredHostReference = "Microsoft.Extensions.Logging.Abstractions";
    private const string MicrosoftExtensionsPublicKeyToken = "adb9793829ddae60";
    private const string UnsupportedMessage = "Native interop is unavailable in Starlight.";
    private static readonly Version PluginVersion = new(4, 1, 10, 0);
    private const string TmdsLibCAssemblyName = "Tmds.LibC";
    private const string LinuxHelperTypeName = "Makaretu.Dns.LinuxHelper";
    private const string ReuseAddressMethodName = "ReuseAddresss";
    private const string NativeLibraryTypeName = "System.Runtime.InteropServices.NativeLibrary";
    private const string MarshalTypeName = "System.Runtime.InteropServices.Marshal";
    private const string StarlightNativeLibraryName = "libstarlight.so";


    private static readonly IReadOnlyDictionary<string, string> RequiredNoticeResourceHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Licenses.PerfectComms-LICENSE.txt"] =
                "20C17D8B8C48A600800DFD14F95D5CB9FF47066A9641DDEAB48DC54AEC96E331",
            ["Licenses.THIRD_PARTY_NOTICES.md"] =
                "98E571B2A4D4325A005607A29929405E153E4ADAB523E29F2698C5D17D7EAC36",
            ["Licenses.SIPSorcery-LICENSE.md"] =
                "C6806C324232D99B9F1BE116B55D376589C817EF22C6A514C1365C428499835A"
        };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly IReadOnlyDictionary<string, Version> ManagedAssemblyVersions =
        new Dictionary<string, Version>(StringComparer.Ordinal)
        {
            ["BouncyCastle.Cryptography"] = new(2, 0, 0, 0),
            ["Common.Logging"] = new(3, 4, 1, 0),
            ["Common.Logging.Core"] = new(3, 4, 1, 0),
            ["Concentus"] = new(2, 2, 2, 0),
            ["DnsClient"] = new(1, 8, 0, 0),
            ["Makaretu.Dns"] = new(2, 0, 1, 0),
            ["Makaretu.Dns.Multicast"] = new(0, 27, 0, 0),
            ["Microsoft.Extensions.DependencyInjection.Abstractions"] = new(8, 0, 0, 0),
            ["Microsoft.Extensions.Logging.Abstractions"] = new(8, 0, 0, 0),
            ["SIPSorcery"] = new(10, 0, 16, 0),
            ["SIPSorceryMedia.Abstractions"] = new(10, 0, 16, 0),
            ["SimpleBase"] = new(1, 3, 1, 0),
            ["System.Net.IPNetwork"] = new(2, 1, 2, 0),
            ["websocket-sharp"] = new(0, 0, 1, 0)
        };

    private static readonly IReadOnlyDictionary<(string Name, Version Version), string>
        PinnedThirdPartyInputHashes =
            new Dictionary<(string Name, Version Version), string>
            {
                [("BouncyCastle.Cryptography", new Version(2, 0, 0, 0))] =
                    "EF92FC661E8D7BA8EC4D39A7CDFCDBA41F14CB327F0D24FE79D0B5BB428899B5",
                [("Common.Logging", new Version(3, 4, 1, 0))] =
                    "CFAA8A06F5AFAC6F7F5825F5AC74F50EE2CA869C131E09377BBCC464EE21C0B2",
                [("Common.Logging.Core", new Version(3, 4, 1, 0))] =
                    "3757ADBAF8AB360E9B3C135AF64FAD025329F0648BACB8076AC66A3EEDDBD10A",
                [("Concentus", new Version(2, 2, 2, 0))] =
                    "3A1DCFCFAF86B90FC2FB6FD0BAACDF0BE70EFEF1C9FBB59683D3BF48949DE1C9",
                [("DnsClient", new Version(1, 8, 0, 0))] =
                    "FCF2D07F01BB34F6E8EA61DA6D281D1979B5D3C1485BCC5B2B350EC8C59CC2F0",
                [("Makaretu.Dns", new Version(2, 0, 1, 0))] =
                    "A5060D45F711F01FE1DFF20568AABDC1014C5B843BE15383EE40CCD4F88E147A",
                [("Makaretu.Dns.Multicast", new Version(0, 27, 0, 0))] =
                    "023391027C668E58B1FFDF2C37448121979D01C3E2722E697BEDCD6819837E8E",
                [("SIPSorcery", new Version(10, 0, 16, 0))] =
                    "702563788B9D7178811FEA30B5432AD76A15DF617E1CD1FD407F297A1CA6453A",
                [("SIPSorceryMedia.Abstractions", new Version(10, 0, 16, 0))] =
                    "3EF101CA8EA078EF53BD94A09F07779121EF28EAB57B83E34B97B7010C071939",
                [("SimpleBase", new Version(1, 3, 1, 0))] =
                    "72B1B02EE7AC700A212216FF5261F1EE5B04CABD5ED56B4E620579C9D7075E10",
                [("System.Net.IPNetwork", new Version(2, 1, 2, 0))] =
                    "14C9961D11769ECBB0BA3B2B8AB18EB8939BF62B2645298AC9F9314D7E932039",
                [("websocket-sharp", new Version(0, 0, 1, 0))] =
                    "AF3B60987EEE8E9B9CB06CD9B9C3C5EBB1F97D2BEF994FC80079BAFB278CA1EF"
            };

    private static readonly IReadOnlySet<string> HostProvidedAssemblies =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Logging.Abstractions"
        };
    private static readonly IReadOnlySet<string> AllowedExternalAssemblyReferences =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "0Harmony|2.10.2.0|",
            "Assembly-CSharp|0.0.0.0|",
            "BepInEx.Core|6.0.0.0|",
            "BepInEx.Unity.IL2CPP|6.0.0.0|",
            "Hazel|1.0.0.0|",
            "Il2CppInterop.Runtime|1.4.6.0|",
            "Il2Cppmscorlib|4.0.0.0|",
            "Microsoft.CSharp|10.0.0.0|b03f5f7f11d50a3a",
            "Microsoft.Extensions.DependencyInjection.Abstractions|8.0.0.0|adb9793829ddae60",
            "Microsoft.Extensions.Logging.Abstractions|8.0.0.0|adb9793829ddae60",
            "Microsoft.Win32.Primitives|8.0.0.0|b03f5f7f11d50a3a",
            "Microsoft.Win32.Registry|8.0.0.0|b03f5f7f11d50a3a",
            "SemanticVersioning|2.0.2.0|a89bb7dc6f7a145c",
            "System.Collections.Concurrent|10.0.0.0|b03f5f7f11d50a3a",
            "System.Collections.NonGeneric|10.0.0.0|b03f5f7f11d50a3a",
            "System.Collections.Specialized|10.0.0.0|b03f5f7f11d50a3a",
            "System.Collections|10.0.0.0|b03f5f7f11d50a3a",
            "System.ComponentModel.Primitives|10.0.0.0|b03f5f7f11d50a3a",
            "System.Console|10.0.0.0|b03f5f7f11d50a3a",
            "System.Diagnostics.Debug|4.0.10.0|b03f5f7f11d50a3a",
            "System.Diagnostics.Process|10.0.0.0|b03f5f7f11d50a3a",
            "System.Diagnostics.TraceSource|8.0.0.0|b03f5f7f11d50a3a",
            "System.Globalization|4.0.10.0|b03f5f7f11d50a3a",
            "System.IO.Compression|10.0.0.0|b77a5c561934e089",
            "System.IO|4.0.10.0|b03f5f7f11d50a3a",
            "System.Linq.Expressions|4.0.10.0|b03f5f7f11d50a3a",
            "System.Linq|10.0.0.0|b03f5f7f11d50a3a",
            "System.Memory|10.0.0.0|cc7b13ffcd2ddd51",
            "System.Net.Http|10.0.0.0|b03f5f7f11d50a3a",
            "System.Net.NameResolution|10.0.0.0|b03f5f7f11d50a3a",
            "System.Net.NetworkInformation|10.0.0.0|b03f5f7f11d50a3a",
            "System.Net.Primitives|10.0.0.0|b03f5f7f11d50a3a",
            "System.Net.Security|10.0.0.0|b03f5f7f11d50a3a",
            "System.Net.Sockets|10.0.0.0|b03f5f7f11d50a3a",
            "System.Net.WebSockets.Client|10.0.0.0|b03f5f7f11d50a3a",
            "System.Net.WebSockets|10.0.0.0|b03f5f7f11d50a3a",
            "System.Numerics.Vectors|8.0.0.0|b03f5f7f11d50a3a",
            "System.Reflection.Emit.ILGeneration|10.0.0.0|b03f5f7f11d50a3a",
            "System.Reflection.Emit.Lightweight|10.0.0.0|b03f5f7f11d50a3a",
            "System.Reflection.Primitives|10.0.0.0|b03f5f7f11d50a3a",
            "System.Reflection.TypeExtensions|4.0.0.0|b03f5f7f11d50a3a",
            "System.Reflection|4.0.10.0|b03f5f7f11d50a3a",
            "System.Runtime.CompilerServices.Unsafe|6.0.0.0|b03f5f7f11d50a3a",
            "System.Runtime.Extensions|4.0.10.0|b03f5f7f11d50a3a",
            "System.Runtime.Intrinsics|10.0.0.0|cc7b13ffcd2ddd51",
            "System.Runtime.InteropServices|10.0.0.0|b03f5f7f11d50a3a",
            "System.Runtime.Numerics|10.0.0.0|b03f5f7f11d50a3a",
            "System.Runtime.Serialization.Formatters|8.1.0.0|b03f5f7f11d50a3a",
            "System.Runtime.Serialization.Primitives|10.0.0.0|b03f5f7f11d50a3a",
            "System.Runtime|4.0.20.0|b03f5f7f11d50a3a",
            "System.Runtime|10.0.0.0|b03f5f7f11d50a3a",
            "System.Security.Cryptography.Algorithms|6.0.0.0|b03f5f7f11d50a3a",
            "System.Security.Cryptography.Csp|6.0.0.0|b03f5f7f11d50a3a",
            "System.Security.Cryptography.Encoding|6.0.0.0|b03f5f7f11d50a3a",
            "System.Security.Cryptography.Primitives|6.0.0.0|b03f5f7f11d50a3a",
            "System.Security.Cryptography.X509Certificates|6.0.0.0|b03f5f7f11d50a3a",
            "System.Security.Cryptography|10.0.0.0|b03f5f7f11d50a3a",
            "System.Text.Encoding.Extensions|10.0.0.0|b03f5f7f11d50a3a",
            "System.Text.Json|10.0.0.0|cc7b13ffcd2ddd51",
            "System.Text.RegularExpressions|10.0.0.0|b03f5f7f11d50a3a",
            "System.Threading.Tasks.Parallel|10.0.0.0|b03f5f7f11d50a3a",
            "System.Threading.ThreadPool|10.0.0.0|b03f5f7f11d50a3a",
            "System.Threading.Thread|10.0.0.0|b03f5f7f11d50a3a",
            "System.Threading|10.0.0.0|b03f5f7f11d50a3a",
            "System.Xml.ReaderWriter|10.0.0.0|b03f5f7f11d50a3a",
            "System.Xml.XDocument|10.0.0.0|b03f5f7f11d50a3a",
            "Unity.TextMeshPro|0.0.0.0|",
            "UnityEngine.AnimationModule|0.0.0.0|",
            "UnityEngine.AudioModule|0.0.0.0|",
            "UnityEngine.CoreModule|0.0.0.0|",
            "UnityEngine.ImageConversionModule|0.0.0.0|",
            "UnityEngine.InputLegacyModule|0.0.0.0|",
            "UnityEngine.Physics2DModule|0.0.0.0|",
            "UnityEngine.UI|1.0.0.0|",
            "UnityEngine.UIModule|0.0.0.0|",
            "netstandard|2.0.0.0|cc7b13ffcd2ddd51"
        };


    private static readonly IReadOnlyDictionary<string, string> StrippedPublicKeyTokens =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Concentus"] = "2f7fb9b49ffdfe20",
            ["DnsClient"] = "4574bb5573c51424"
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ExpectedImports =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["SIPSorcery"] = ImportSet(
                "Polyfills.Polyfill/StandardHandleHelper::GetStdHandle|kernel32.dll|GetStdHandle",
                "Polyfills.Polyfill/HardLinkHelper::CreateHardLinkW|kernel32.dll|CreateHardLinkW",
                "Polyfills.Polyfill/HardLinkHelper::link|libc|link"),
            ["Concentus"] = ImportSet(
                "Concentus.Native.KernelInteropLinux/LibDL::dlclose|libdl.so|dlclose",
                "Concentus.Native.KernelInteropLinux/LibDL::dlerror|libdl.so|dlerror",
                "Concentus.Native.KernelInteropLinux/LibDL::<dlopen>g____PInvoke|3_0|libdl.so|dlopen",
                "Concentus.Native.KernelInteropLinux/LibDL2::dlclose|libdl.so.2|dlclose",
                "Concentus.Native.KernelInteropLinux/LibDL2::dlerror|libdl.so.2|dlerror",
                "Concentus.Native.KernelInteropLinux/LibDL2::<dlopen>g____PInvoke|3_0|libdl.so.2|dlopen",
                "Concentus.Native.KernelInteropMacOS::dlclose|libSystem.dylib|dlclose",
                "Concentus.Native.KernelInteropMacOS::dlerror|libSystem.dylib|dlerror",
                "Concentus.Native.KernelInteropMacOS::<dlopen>g____PInvoke|1_0|libSystem.dylib|dlopen",
                "Concentus.Native.KernelInteropWindows::GetLastError|kernel32.dll|GetLastError",
                "Concentus.Native.KernelInteropWindows::GetSystemInfo|kernel32.dll|GetSystemInfo",
                "Concentus.Native.KernelInteropWindows::<LoadLibraryExW>g____PInvoke|8_0|kernel32.dll|LoadLibraryExW",
                "Concentus.Native.KernelInteropWindows::<FreeLibrary>g____PInvoke|9_0|kernel32.dll|FreeLibrary",
                "Concentus.Native.NativeOpus::opus_decode|opus|opus_decode",
                "Concentus.Native.NativeOpus::opus_decode_float|opus|opus_decode_float",
                "Concentus.Native.NativeOpus::opus_multistream_decode|opus|opus_multistream_decode",
                "Concentus.Native.NativeOpus::opus_multistream_decode_float|opus|opus_multistream_decode_float",
                "Concentus.Native.NativeOpus::opus_encode|opus|opus_encode",
                "Concentus.Native.NativeOpus::opus_encode_float|opus|opus_encode_float",
                "Concentus.Native.NativeOpus::opus_multistream_encode|opus|opus_multistream_encode",
                "Concentus.Native.NativeOpus::opus_multistream_encode_float|opus|opus_multistream_encode_float",
                "Concentus.Native.NativeOpus::opus_decoder_ctl|opus|opus_decoder_ctl",
                "Concentus.Native.NativeOpus::opus_encoder_ctl|opus|opus_encoder_ctl",
                "Concentus.Native.NativeOpus::opus_multistream_decoder_ctl|opus|opus_multistream_decoder_ctl",
                "Concentus.Native.NativeOpus::opus_multistream_encoder_ctl|opus|opus_multistream_encoder_ctl",
                "Concentus.Native.NativeOpus::opus_get_version_string|opus|opus_get_version_string",
                "Concentus.Native.NativeOpus::opus_encoder_destroy|opus|opus_encoder_destroy",
                "Concentus.Native.NativeOpus::opus_multistream_encoder_destroy|opus|opus_multistream_encoder_destroy",
                "Concentus.Native.NativeOpus::opus_decoder_destroy|opus|opus_decoder_destroy",
                "Concentus.Native.NativeOpus::opus_multistream_decoder_destroy|opus|opus_multistream_decoder_destroy",
                "Concentus.Native.NativeOpus::<opus_decoder_create>g____PInvoke|41_0|opus|opus_decoder_create",
                "Concentus.Native.NativeOpus::<opus_multistream_decoder_create>g____PInvoke|42_0|opus|opus_multistream_decoder_create",
                "Concentus.Native.NativeOpus::<opus_encoder_create>g____PInvoke|47_0|opus|opus_encoder_create",
                "Concentus.Native.NativeOpus::<opus_multistream_surround_encoder_create>g____PInvoke|48_0|opus|opus_multistream_surround_encoder_create",
                "Concentus.Native.NativeOpus::<opus_decoder_ctl>g____PInvoke|54_0|opus|opus_decoder_ctl",
                "Concentus.Native.NativeOpus::<opus_encoder_ctl>g____PInvoke|56_0|opus|opus_encoder_ctl",
                "Concentus.Native.NativeOpus::<opus_multistream_decoder_ctl>g____PInvoke|58_0|opus|opus_multistream_decoder_ctl",
                "Concentus.Native.NativeOpus::<opus_multistream_encoder_ctl>g____PInvoke|60_0|opus|opus_multistream_encoder_ctl"),
            ["DnsClient"] = ImportSet(
                "Interop/IpHlpApi::GetAdaptersAddresses|iphlpapi.dll|GetAdaptersAddresses",
                "Interop/IpHlpApi::GetNetworkParams|iphlpapi.dll|GetNetworkParams")
        };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw new InvalidOperationException("Expected prepare, merge, self-test, or validate command.");
            }

            var command = args[0];
            var options = ParseOptions(args.AsSpan(1));
            switch (command)
            {
                case "prepare":
                    Prepare(
                        RequiredOption(options, "media"),
                        RequiredOption(options, "output"));
                    break;
                case "merge":
                    Merge(
                        RequiredOption(options, "plugin"),
                        RequiredOption(options, "inputs"),
                        RequiredOption(options, "references"),
                        RequiredOption(options, "output"));
                    break;
                case "self-test":
                    RunAssemblyValidatorSelfTest();
                    break;
                case "validate":
                    ValidateMergedAssembly(RequiredOption(options, "assembly"));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown command '{command}'.");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void RunAssemblyValidatorSelfTest()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"perfectcomms-starlight-validator-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var cases = new[]
            {
                CreatePInvokeValidatorCase(
                    directory,
                    "approved-capture-imports",
                    true,
                    ("libstarlight.so", "HasPermission", "starlight_voice_has_record_audio_permission"),
                    ("libstarlight.so", "RequestPermission", "starlight_voice_request_record_audio_permission"),
                    ("libstarlight.so", "StartCapture", "starlight_voice_start_capture"),
                    ("libstarlight.so", "ReadFloat", "starlight_voice_read_float"),
                    ("libstarlight.so", "StopCapture", "starlight_voice_stop_capture"),
                    ("libstarlight.so", "IsCaptureRunning", "starlight_voice_is_capture_running"),
                    ("libstarlight.so", "GetSampleRate", "starlight_voice_get_sample_rate"),
                    ("libstarlight.so", "GetBufferFrames", "starlight_voice_get_buffer_frames"),
                    ("libstarlight.so", "GetLastError", "starlight_voice_get_last_error")),
                CreatePInvokeValidatorCase(
                    directory,
                    "library-alias",
                    false,
                    ("starlight", "ReadFloat", "starlight_voice_read_float")),
                CreatePInvokeValidatorCase(
                    directory,
                    "pc-mobile",
                    false,
                    ("pc_mobile", "GetWidth", "get_width")),
                CreatePInvokeValidatorCase(
                    directory,
                    "libc",
                    false,
                    ("libc", "Link", "link")),
                CreateMutatedCaptureAbiValidatorCase(
                    directory,
                    "capture-wrong-return-type",
                    "starlight_voice_read_float",
                    static method => method.ReturnType = method.Module.TypeSystem.Int64),
                CreateMutatedCaptureAbiValidatorCase(
                    directory,
                    "boolean-without-i1",
                    "starlight_voice_is_capture_running",
                    static method => method.MethodReturnType.MarshalInfo = null),
                CreateMutatedCaptureAbiValidatorCase(
                    directory,
                    "float-array-without-out",
                    "starlight_voice_read_float",
                    static method => method.Parameters[0].Attributes = ParameterAttributes.None),
                CreateMutatedCaptureAbiValidatorCase(
                    directory,
                    "capture-wrong-calling-convention",
                    "starlight_voice_get_sample_rate",
                    static method => method.PInvokeInfo!.Attributes = PInvokeAttributes.CallConvStdCall),
                CreateMutatedCaptureAbiValidatorCase(
                    directory,
                    "capture-missing-preserve-sig",
                    "starlight_voice_get_buffer_frames",
                    static method => method.ImplAttributes = MethodImplAttributes.IL),
                CreatePInvokeValidatorCase(
                    directory,
                    "kernel32",
                    false,
                    ("kernel32.dll", "GetStdHandle", "GetStdHandle")),
                CreatePInvokeValidatorCase(
                    directory,
                    "unapproved-entry-point",
                    false,
                    ("libstarlight.so", "Initialize", "starlight_voice_initialize")),
                CreateNativeLibraryValidatorCase(directory, "native-library-call", OpCodes.Call),
                CreateNativeLibraryValidatorCase(directory, "native-library-callvirt", OpCodes.Callvirt),
                CreateCalliValidatorCase(directory),
                CreateMarshalDelegateValidatorCase(directory)
            };

            foreach (var validatorCase in cases)
            {
                var actual = StarlightAssemblyValidator.IsAssemblySafe(validatorCase.Path);
                if (actual != validatorCase.Expected)
                {
                    throw new InvalidOperationException(
                        $"Starlight validator self-test failed: {validatorCase.Name}.");
                }
            }
            RunManagedClosureSelfTests();
            RunResourceSelfTests();

            var unreadablePath = Path.Combine(directory, "unreadable.dll");
            File.WriteAllBytes(unreadablePath, [0x00, 0x01, 0x02, 0x03]);
            if (StarlightAssemblyValidator.IsAssemblySafe(unreadablePath))
            {
                throw new InvalidOperationException("Starlight validator self-test failed: unreadable.");
            }

            Console.WriteLine("starlight.validator.self-test.ok");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static (string Name, string Path, bool Expected) CreatePInvokeValidatorCase(
        string directory,
        string name,
        bool expected,
        params (string Library, string Method, string EntryPoint)[] imports)
    {
        var path = Path.Combine(directory, name + ".dll");
        using var assembly = CreateValidatorFixture(name);
        var nestedType = assembly.MainModule.Types.Single(static type => type.Name == "Fixture")
            .NestedTypes.Single();

        foreach (var import in imports)
        {
            var moduleReference = new ModuleReference(import.Library);
            assembly.MainModule.ModuleReferences.Add(moduleReference);
            var method = new MethodDefinition(
                import.Method,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PInvokeImpl,
                assembly.MainModule.TypeSystem.Int32)
            {
                ImplAttributes = MethodImplAttributes.PreserveSig,
                PInvokeInfo = new PInvokeInfo(
                    PInvokeAttributes.CallConvCdecl,
                    import.EntryPoint,
                    moduleReference)
            };
            nestedType.Methods.Add(method);
            if (expected)
            {
                ApplyCaptureAbi(method, import.EntryPoint);
            }
        }

        assembly.Write(path);
        return (name, path, expected);
    }

    private static (string Name, string Path, bool Expected) CreateMutatedCaptureAbiValidatorCase(
        string directory,
        string name,
        string entryPoint,
        Action<MethodDefinition> mutate)
    {
        var path = Path.Combine(directory, name + ".dll");
        using var assembly = CreateValidatorFixture(name);
        var nestedType = assembly.MainModule.Types.Single(static type => type.Name == "Fixture")
            .NestedTypes.Single();
        var moduleReference = new ModuleReference("libstarlight.so");
        assembly.MainModule.ModuleReferences.Add(moduleReference);
        MethodDefinition? target = null;
        foreach (var (methodName, captureEntryPoint) in new[]
                 {
                     ("HasPermission", "starlight_voice_has_record_audio_permission"),
                     ("RequestPermission", "starlight_voice_request_record_audio_permission"),
                     ("StartCapture", "starlight_voice_start_capture"),
                     ("ReadFloat", "starlight_voice_read_float"),
                     ("StopCapture", "starlight_voice_stop_capture"),
                     ("IsCaptureRunning", "starlight_voice_is_capture_running"),
                     ("GetSampleRate", "starlight_voice_get_sample_rate"),
                     ("GetBufferFrames", "starlight_voice_get_buffer_frames"),
                     ("GetLastError", "starlight_voice_get_last_error")
                 })
        {
            var method = new MethodDefinition(
                methodName,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PInvokeImpl,
                assembly.MainModule.TypeSystem.Int32)
            {
                ImplAttributes = MethodImplAttributes.PreserveSig,
                PInvokeInfo = new PInvokeInfo(
                    PInvokeAttributes.CallConvCdecl,
                    captureEntryPoint,
                    moduleReference)
            };
            nestedType.Methods.Add(method);
            ApplyCaptureAbi(method, captureEntryPoint);
            if (string.Equals(captureEntryPoint, entryPoint, StringComparison.Ordinal))
            {
                target = method;
            }
        }

        mutate(target ?? throw new InvalidOperationException($"Unknown capture ABI mutation '{entryPoint}'."));
        assembly.Write(path);
        return (name, path, false);
    }

    private static void ApplyCaptureAbi(MethodDefinition method, string entryPoint)
    {
        switch (entryPoint)
        {
            case "starlight_voice_has_record_audio_permission":
            case "starlight_voice_request_record_audio_permission":
            case "starlight_voice_is_capture_running":
                method.ReturnType = method.Module.TypeSystem.Boolean;
                AddI1MarshalInfo(method);
                break;
            case "starlight_voice_start_capture":
                method.ReturnType = method.Module.TypeSystem.Int32;
                method.Parameters.Add(new ParameterDefinition(method.Module.TypeSystem.Int32));
                method.Parameters.Add(new ParameterDefinition(method.Module.TypeSystem.Int32));
                break;
            case "starlight_voice_read_float":
                method.ReturnType = method.Module.TypeSystem.Int32;
                method.Parameters.Add(
                    new ParameterDefinition(new ArrayType(method.Module.TypeSystem.Single))
                    {
                        Attributes = ParameterAttributes.Out
                    });
                method.Parameters.Add(new ParameterDefinition(method.Module.TypeSystem.Int32));
                break;
            case "starlight_voice_stop_capture":
                method.ReturnType = method.Module.TypeSystem.Void;
                break;
            case "starlight_voice_get_sample_rate":
            case "starlight_voice_get_buffer_frames":
                method.ReturnType = method.Module.TypeSystem.Int32;
                break;
            case "starlight_voice_get_last_error":
                method.ReturnType = method.Module.TypeSystem.IntPtr;
                break;
            default:
                throw new InvalidOperationException($"Unknown capture ABI fixture '{entryPoint}'.");
        }
    }

    private static void AddI1MarshalInfo(MethodDefinition method)
    {
        method.MethodReturnType.MarshalInfo = new MarshalInfo(NativeType.I1);
    }

    private static (string Name, string Path, bool Expected) CreateNativeLibraryValidatorCase(
        string directory,
        string name,
        OpCode operation)
    {
        var path = Path.Combine(directory, name + ".dll");
        using var assembly = CreateValidatorFixture(name);
        var nestedType = assembly.MainModule.Types.Single(static type => type.Name == "Fixture")
            .NestedTypes.Single();
        var method = new MethodDefinition(
            "InvokeNativeLibrary",
            MethodAttributes.Public | MethodAttributes.Static,
            assembly.MainModule.TypeSystem.Void);
        nestedType.Methods.Add(method);
        method.Body = new MethodBody(method);

        var nativeLibraryType = new TypeReference(
            "System.Runtime.InteropServices",
            "NativeLibrary",
            assembly.MainModule,
            assembly.MainModule.TypeSystem.CoreLibrary);
        var calledMethod = new MethodReference(
            "Load",
            assembly.MainModule.TypeSystem.IntPtr,
            nativeLibraryType);
        calledMethod.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));

        var processor = method.Body.GetILProcessor();
        processor.Emit(OpCodes.Ldstr, "libstarlight.so");
        processor.Emit(operation, calledMethod);
        processor.Emit(OpCodes.Pop);
        processor.Emit(OpCodes.Ret);
        assembly.Write(path);
        return (name, path, false);
    }

    private static (string Name, string Path, bool Expected) CreateCalliValidatorCase(string directory)
    {
        const string name = "unmanaged-calli";
        var path = Path.Combine(directory, name + ".dll");
        using var assembly = CreateValidatorFixture(name);
        var nestedType = assembly.MainModule.Types.Single(static type => type.Name == "Fixture")
            .NestedTypes.Single();
        var method = new MethodDefinition(
            "InvokeFunctionPointer",
            MethodAttributes.Public | MethodAttributes.Static,
            assembly.MainModule.TypeSystem.Void);
        nestedType.Methods.Add(method);
        method.Body = new MethodBody(method);
        var callSite = new CallSite(assembly.MainModule.TypeSystem.Void)
        {
            CallingConvention = MethodCallingConvention.C
        };
        var processor = method.Body.GetILProcessor();
        processor.Emit(OpCodes.Ldc_I4_0);
        processor.Emit(OpCodes.Conv_I);
        processor.Emit(OpCodes.Calli, callSite);
        processor.Emit(OpCodes.Ret);
        assembly.Write(path);
        return (name, path, false);
    }

    private static (string Name, string Path, bool Expected) CreateMarshalDelegateValidatorCase(
        string directory)
    {
        const string name = "marshal-delegate-bridge";
        var path = Path.Combine(directory, name + ".dll");
        using var assembly = CreateValidatorFixture(name);
        var nestedType = assembly.MainModule.Types.Single(static type => type.Name == "Fixture")
            .NestedTypes.Single();
        var method = new MethodDefinition(
            "CreateDelegate",
            MethodAttributes.Public | MethodAttributes.Static,
            assembly.MainModule.TypeSystem.Void);
        nestedType.Methods.Add(method);
        method.Body = new MethodBody(method);
        var marshalType = new TypeReference(
            "System.Runtime.InteropServices",
            "Marshal",
            assembly.MainModule,
            assembly.MainModule.TypeSystem.CoreLibrary);
        var bridge = new MethodReference(
            "GetDelegateForFunctionPointer",
            assembly.MainModule.TypeSystem.Object,
            marshalType);
        bridge.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.IntPtr));
        bridge.Parameters.Add(
            new ParameterDefinition(
                new TypeReference(
                    "System",
                    "Type",
                    assembly.MainModule,
                    assembly.MainModule.TypeSystem.CoreLibrary)));
        var processor = method.Body.GetILProcessor();
        processor.Emit(OpCodes.Ldc_I4_0);
        processor.Emit(OpCodes.Conv_I);
        processor.Emit(OpCodes.Ldnull);
        processor.Emit(OpCodes.Call, bridge);
        processor.Emit(OpCodes.Pop);
        processor.Emit(OpCodes.Ret);
        assembly.Write(path);
        return (name, path, false);
    }

    private static void RunManagedClosureSelfTests()
    {
        using var fixture = CreateValidatorFixture("managed-closure");
        fixture.MainModule.AssemblyReferences.Clear();

        fixture.MainModule.AssemblyReferences.Add(
            AssemblyReference(
                RequiredHostReference,
                new Version(8, 0, 0, 0),
                MicrosoftExtensionsPublicKeyToken));
        ValidateExternalAssemblyReferences(fixture.MainModule);

        fixture.MainModule.AssemblyReferences.Clear();
        fixture.MainModule.AssemblyReferences.Add(
            AssemblyReference(
                "System.Runtime",
                new Version(10, 0, 0, 0),
                "b03f5f7f11d50a3a"));
        ValidateExternalAssemblyReferences(fixture.MainModule);

        fixture.MainModule.AssemblyReferences.Clear();
        fixture.MainModule.AssemblyReferences.Add(
            new AssemblyNameReference("Bogus.External", new Version(1, 0, 0, 0)));
        ExpectInvalidOperation(
            "bogus-external-reference",
            () => ValidateExternalAssemblyReferences(fixture.MainModule));

        fixture.MainModule.AssemblyReferences.Clear();
        fixture.MainModule.AssemblyReferences.Add(
            new AssemblyNameReference(TmdsLibCAssemblyName, new Version(0, 2, 0, 0)));
        ExpectInvalidOperation(
            "tmds-reference",
            () => ValidateNoTmdsMetadata(fixture.MainModule));

        fixture.MainModule.AssemblyReferences.Clear();
        fixture.MainModule.AssemblyReferences.Add(
            AssemblyReference(
                RequiredHostReference,
                new Version(10, 0, 0, 0),
                MicrosoftExtensionsPublicKeyToken));
        ExpectInvalidOperation(
            "host-version-mismatch",
            () => ValidateExternalAssemblyReferences(fixture.MainModule));

        fixture.MainModule.AssemblyReferences.Clear();
        fixture.MainModule.AssemblyReferences.Add(
            AssemblyReference(
                RequiredHostReference,
                new Version(8, 0, 0, 0),
                "0000000000000000"));
        ExpectInvalidOperation(
            "host-token-mismatch",
            () => ValidateExternalAssemblyReferences(fixture.MainModule));
        foreach (var flag in new[] { AssemblyAttributes.Retargetable, AssemblyAttributes.WindowsRuntime })
        {
            fixture.MainModule.AssemblyReferences.Clear();
            var flagged = AssemblyReference(
                RequiredHostReference,
                new Version(8, 0, 0, 0),
                MicrosoftExtensionsPublicKeyToken);
            flagged.Attributes = flag;
            fixture.MainModule.AssemblyReferences.Add(flagged);
            ExpectInvalidOperation(
                $"host-reference-flags-{flag}",
                () => ValidateExternalAssemblyReferences(fixture.MainModule));
        }
        RunTmdsRewriteSelfTest();
    }

    private static AssemblyNameReference AssemblyReference(
        string name,
        Version version,
        string publicKeyToken)
    {
        return new AssemblyNameReference(name, version)
        {
            PublicKeyToken = Convert.FromHexString(publicKeyToken)
        };
    }

    private static void RunTmdsRewriteSelfTest()
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Makaretu.Dns.Multicast", new Version(0, 27, 0, 0)),
            "Makaretu.Dns.Multicast",
            ModuleKind.Dll);
        var socketAssembly = new AssemblyNameReference("System.Net.Sockets", new Version(10, 0, 0, 0));
        var tmdsAssembly = new AssemblyNameReference(TmdsLibCAssemblyName, new Version(0, 2, 0, 0));
        assembly.MainModule.AssemblyReferences.Add(socketAssembly);
        assembly.MainModule.AssemblyReferences.Add(tmdsAssembly);
        var socketType = new TypeReference(
            "System.Net.Sockets",
            "Socket",
            assembly.MainModule,
            socketAssembly);
        var linuxHelper = new TypeDefinition(
            "Makaretu.Dns",
            "LinuxHelper",
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed,
            assembly.MainModule.TypeSystem.Object);
        assembly.MainModule.Types.Add(linuxHelper);
        var method = new MethodDefinition(
            ReuseAddressMethodName,
            MethodAttributes.Public | MethodAttributes.Static,
            assembly.MainModule.TypeSystem.Void);
        method.Parameters.Add(new ParameterDefinition(socketType));
        method.Body = new MethodBody(method);
        linuxHelper.Methods.Add(method);

        var libcType = new TypeReference("Tmds.Linux", "LibC", assembly.MainModule, tmdsAssembly);
        var socklenType = new TypeReference("Tmds.Linux", "socklen_t", assembly.MainModule, tmdsAssembly);
        var processor = method.Body.GetILProcessor();
        foreach (var (declaringType, name) in new[]
                 {
                     (libcType, "get_SOL_SOCKET"),
                     (libcType, "get_SO_REUSEADDR"),
                     (socklenType, "op_Implicit"),
                     (libcType, "setsockopt")
                 })
        {
            processor.Emit(
                OpCodes.Call,
                new MethodReference(name, assembly.MainModule.TypeSystem.Int32, declaringType));
            processor.Emit(OpCodes.Pop);
        }
        processor.Emit(OpCodes.Ret);

        if (!RewriteUnusedLinuxSocketReuse(assembly) ||
            assembly.MainModule.AssemblyReferences.Any(static reference =>
                reference.Name == TmdsLibCAssemblyName) ||
            !method.Body.Instructions.Any(static instruction =>
                instruction.Operand is MethodReference reference &&
                reference.Name == "SetSocketOption") ||
            method.Body.Instructions.Any(static instruction =>
                instruction.Operand is MethodReference reference &&
                IsTmdsType(reference.DeclaringType)))
        {
            throw new InvalidOperationException(
                "Starlight validator self-test failed: Tmds.LibC rewrite.");
        }

    }

    private static void RunResourceSelfTests()
    {
        using var valid = CreateValidatorFixture("resource-valid");
        valid.MainModule.Resources.Add(
            new EmbeddedResource(
                "neutral",
                ManifestResourceAttributes.Private,
                StrictUtf8.GetBytes("known managed resource")));
        ValidateResources(valid.MainModule);
        using var managedContainer = CreateValidatorFixture("resource-container-valid");
        managedContainer.MainModule.Resources.Add(
            new EmbeddedResource(
                "PerfectComms.Strings.resources",
                ManifestResourceAttributes.Private,
                CreateManagedResourceContainer("message", "known managed resource")));
        ValidateResources(managedContainer.MainModule);

        foreach (var (entryName, value) in new[]
                 {
                     ("neutral", new byte[] { 0xca, 0xfe, 0xba, 0xbf }),
                     ("payload.so", StrictUtf8.GetBytes("not native magic"))
                 })
        {
            using var invalidContainer = CreateValidatorFixture($"resource-container-{entryName}");
            invalidContainer.MainModule.Resources.Add(
                new EmbeddedResource(
                    entryName == "neutral" ? "neutral-container" : "PerfectComms.Payloads.resources",
                    ManifestResourceAttributes.Private,
                    CreateManagedResourceContainer(entryName, value)));
            ExpectInvalidOperation(
                $"managed-resource-native-{entryName}",
                () => ValidateResources(invalidContainer.MainModule));
        }

        using var nativeStreamPayload = new MemoryStream(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' });
        using var invalidStreamContainer = CreateValidatorFixture("resource-container-stream");
        invalidStreamContainer.MainModule.Resources.Add(
            new EmbeddedResource(
                "PerfectComms.Streams.resources",
                ManifestResourceAttributes.Private,
                CreateManagedResourceContainer("neutral-stream", nativeStreamPayload)));
        ExpectInvalidOperation(
            "managed-resource-native-stream",
            () => ValidateResources(invalidStreamContainer.MainModule));


        var nativeMagic = new[]
        {
            new byte[] { (byte)'M', (byte)'Z' },
            new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' },
            new byte[] { 0xca, 0xfe, 0xba, 0xbe },
            new byte[] { 0xbe, 0xba, 0xfe, 0xca },
            new byte[] { 0xca, 0xfe, 0xba, 0xbf },
            new byte[] { 0xbf, 0xba, 0xfe, 0xca }
        };
        for (var index = 0; index < nativeMagic.Length; index++)
        {
            using var invalid = CreateValidatorFixture($"resource-native-{index}");
            invalid.MainModule.Resources.Add(
                new EmbeddedResource(
                    $"neutral-{index}",
                    ManifestResourceAttributes.Private,
                    nativeMagic[index]));
            ExpectInvalidOperation(
                $"neutral-native-resource-{index}",
                () => ValidateResources(invalid.MainModule));
        }
    }

    private static byte[] CreateManagedResourceContainer(string name, object value)
    {
        using var stream = new MemoryStream();
        using var writer = new System.Resources.ResourceWriter(stream);
        if (value is Stream valueStream)
        {
            writer.AddResource(name, valueStream);
        }
        else
        {
            writer.AddResource(name, value);
        }
        writer.Generate();
        return stream.ToArray();
    }

    private static void ExpectInvalidOperation(string name, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException($"Starlight validator self-test failed: {name}.");
    }

    private static AssemblyDefinition CreateValidatorFixture(string name)
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)),
            name,
            ModuleKind.Dll);
        var outerType = new TypeDefinition(
            "PerfectComms.ValidatorFixtures",
            "Fixture",
            TypeAttributes.NotPublic | TypeAttributes.Class,
            assembly.MainModule.TypeSystem.Object);
        var nestedType = new TypeDefinition(
            string.Empty,
            "Nested",
            TypeAttributes.NestedPrivate | TypeAttributes.Class,
            assembly.MainModule.TypeSystem.Object);
        outerType.NestedTypes.Add(nestedType);
        assembly.MainModule.Types.Add(outerType);
        return assembly;
    }

    private static void Prepare(string mediaPath, string outputDirectory)
    {
        mediaPath = Path.GetFullPath(mediaPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        RequireFile(mediaPath);

        var dependencyPath = Path.ChangeExtension(mediaPath, ".deps.json");
        RequireFile(dependencyPath);
        var runtimeFiles = ReadRuntimeFiles(dependencyPath);
        var mediaFileName = Path.GetFileName(mediaPath);
        if (!runtimeFiles.Contains(mediaFileName))
        {
            throw new InvalidOperationException($"Dependency file does not contain '{mediaFileName}'.");
        }

        runtimeFiles.RemoveWhere(static fileName =>
            HostProvidedAssemblies.Contains(Path.GetFileNameWithoutExtension(fileName)));

        RecreateDirectory(outputDirectory);
        foreach (var fileName in runtimeFiles.OrderBy(static value => value, StringComparer.Ordinal))
        {
            var inputPath = Path.Combine(Path.GetDirectoryName(mediaPath)!, fileName);
            RequireFile(inputPath);
            var outputPath = Path.Combine(outputDirectory, fileName);
            SanitizeAssembly(inputPath, outputPath, fileName == mediaFileName);
        }

        ValidateInputDirectory(outputDirectory, true);
    }

    private static SortedSet<string> ReadRuntimeFiles(string dependencyPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(dependencyPath));
        var root = document.RootElement;
        var targetName = root.GetProperty("runtimeTarget").GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            throw new InvalidOperationException("Dependency file has no runtime target.");
        }

        var target = root.GetProperty("targets").GetProperty(targetName);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var library in target.EnumerateObject())
        {
            if (library.Value.TryGetProperty("native", out var native) && native.EnumerateObject().Any())
            {
                throw new InvalidOperationException($"Native dependency assets are prohibited: {library.Name}.");
            }

            if (library.Value.TryGetProperty("runtimeTargets", out var runtimeTargets) && runtimeTargets.EnumerateObject().Any())
            {
                throw new InvalidOperationException($"Runtime-specific dependency assets are prohibited: {library.Name}.");
            }

            if (!library.Value.TryGetProperty("runtime", out var runtime))
            {
                continue;
            }

            foreach (var asset in runtime.EnumerateObject())
            {
                if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unexpected runtime asset '{asset.Name}' in {library.Name}.");
                }

                var fileName = Path.GetFileName(asset.Name.Replace('/', Path.DirectorySeparatorChar));
                if (!files.Add(fileName))
                {
                    throw new InvalidOperationException($"Duplicate runtime assembly filename '{fileName}'.");
                }
            }
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("Dependency file contains no managed runtime assemblies.");
        }

        return files;
    }

    private static void SanitizeAssembly(string inputPath, string outputPath, bool allowMediaAssembly)
    {
        var inputBytes = File.ReadAllBytes(inputPath);
        using (var peStream = new MemoryStream(inputBytes, false))
        {
            StarlightAssemblyValidator.ValidateStrictPe(peStream, inputPath);
        }

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(inputPath)!);
        using var assemblyStream = new MemoryStream(inputBytes, false);
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyStream, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false,
            AssemblyResolver = resolver,
        });

        ValidateAssemblyIdentity(assembly.Name, allowMediaAssembly);
        ValidatePinnedThirdPartyInput(inputBytes, assembly.Name, allowMediaAssembly);
        ValidateResources(assembly.MainModule);
        var changed = RewriteUnusedLinuxSocketReuse(assembly);
        changed |= RewriteImports(assembly);
        changed |= StripStrongName(assembly);
        changed |= RewriteStrongAssemblyReferences(assembly.MainModule);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (!changed)
        {
            File.WriteAllBytes(outputPath, inputBytes);
        }
        else
        {
            var sourceHash = SHA256.HashData(inputBytes);
            assembly.MainModule.Mvid = DeterministicGuid(sourceHash);
            var temporaryPath = outputPath + ".tmp";
            File.Delete(temporaryPath);
            assembly.Write(temporaryPath, new WriterParameters
            {
                WriteSymbols = false,
                Timestamp = DeterministicTimestamp(sourceHash)
            });
            File.Move(temporaryPath, outputPath, true);
        }

        ValidateSanitizedAssembly(
            outputPath,
            allowMediaAssembly,
            Path.GetDirectoryName(inputPath));
    }

    private static void ValidateAssemblyIdentity(AssemblyNameDefinition name, bool allowMediaAssembly)
    {
        if (allowMediaAssembly && string.Equals(name.Name, MediaAssemblyName, StringComparison.Ordinal))
        {
            return;
        }

        if (!ManagedAssemblyVersions.TryGetValue(name.Name, out var expectedVersion))
        {
            throw new InvalidOperationException($"Unexpected managed runtime assembly '{name.FullName}'.");
        }

        if (name.Version != expectedVersion)
        {
            throw new InvalidOperationException(
                $"Unexpected version for {name.Name}: expected {expectedVersion}, found {name.Version}.");
        }
    }

    private static void ValidatePinnedThirdPartyInput(
        byte[] inputBytes,
        AssemblyNameDefinition name,
        bool allowMediaAssembly)
    {
        if (allowMediaAssembly && string.Equals(name.Name, MediaAssemblyName, StringComparison.Ordinal))
        {
            return;
        }

        if (!PinnedThirdPartyInputHashes.TryGetValue((name.Name, name.Version), out var expectedHash))
        {
            throw new InvalidOperationException(
                $"No pinned input hash exists for '{name.Name}, Version={name.Version}'.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(inputBytes));
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pinned input hash mismatch for '{name.Name}, Version={name.Version}': " +
                $"expected {expectedHash}, found {actualHash}.");
        }
    }

    private static bool RewriteUnusedLinuxSocketReuse(AssemblyDefinition assembly)
    {
        if (!string.Equals(assembly.Name.Name, "Makaretu.Dns.Multicast", StringComparison.Ordinal))
        {
            return false;
        }

        var linuxHelper = AllTypes(assembly.MainModule)
            .SingleOrDefault(static type => type.FullName == LinuxHelperTypeName);
        var method = linuxHelper?.Methods.SingleOrDefault(static candidate =>
            candidate.Name == ReuseAddressMethodName &&
            candidate.IsStatic &&
            candidate.ReturnType.FullName == "System.Void" &&
            candidate.Parameters.Count == 1 &&
            candidate.Parameters[0].ParameterType.FullName == "System.Net.Sockets.Socket");
        if (method is null || !method.HasBody)
        {
            throw new InvalidOperationException(
                $"{LinuxHelperTypeName}.{ReuseAddressMethodName}(Socket) was not found in the pinned multicast input.");
        }

        var tmdsCalls = method.Body.Instructions
            .Select(static instruction => instruction.Operand)
            .OfType<MethodReference>()
            .Where(static reference => IsTmdsType(reference.DeclaringType))
            .Select(static reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!tmdsCalls.SetEquals(new[] { "get_SOL_SOCKET", "get_SO_REUSEADDR", "op_Implicit", "setsockopt" }))
        {
            throw new InvalidOperationException(
                $"{LinuxHelperTypeName}.{ReuseAddressMethodName}(Socket) no longer has the pinned Tmds.LibC implementation.");
        }

        var socketType = method.Parameters[0].ParameterType;
        var socketOptionLevel = new TypeReference(
            "System.Net.Sockets",
            "SocketOptionLevel",
            assembly.MainModule,
            socketType.Scope,
            true);
        var socketOptionName = new TypeReference(
            "System.Net.Sockets",
            "SocketOptionName",
            assembly.MainModule,
            socketType.Scope,
            true);
        var setSocketOption = new MethodReference(
            "SetSocketOption",
            assembly.MainModule.TypeSystem.Void,
            socketType)
        {
            HasThis = true
        };
        setSocketOption.Parameters.Add(new ParameterDefinition(socketOptionLevel));
        setSocketOption.Parameters.Add(new ParameterDefinition(socketOptionName));
        setSocketOption.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.Boolean));

        method.Body = new MethodBody(method);
        var processor = method.Body.GetILProcessor();
        processor.Emit(OpCodes.Ldarg_0);
        processor.Emit(OpCodes.Ldc_I4, 65535);
        processor.Emit(OpCodes.Ldc_I4_4);
        processor.Emit(OpCodes.Ldc_I4_1);
        processor.Emit(OpCodes.Callvirt, setSocketOption);
        processor.Emit(OpCodes.Ret);

        var tmdsReferences = assembly.MainModule.AssemblyReferences
            .Where(static reference => reference.Name == TmdsLibCAssemblyName)
            .ToArray();
        if (tmdsReferences.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one {TmdsLibCAssemblyName} reference in the pinned multicast input.");
        }

        assembly.MainModule.AssemblyReferences.Remove(tmdsReferences[0]);
        return true;
    }

    private static bool RewriteImports(AssemblyDefinition assembly)
    {
        var expected = ExpectedImports.TryGetValue(assembly.Name.Name, out var imports)
            ? imports
            : ImportSet();
        var found = new HashSet<string>(StringComparer.Ordinal);
        var methods = AllTypes(assembly.MainModule).SelectMany(static type => type.Methods).ToArray();

        foreach (var method in methods)
        {
            if (!method.IsPInvokeImpl && method.PInvokeInfo is null)
            {
                continue;
            }

            if (method.PInvokeInfo is null)
            {
                throw new InvalidOperationException($"Malformed P/Invoke method '{method.FullName}'.");
            }

            var key = ImportKey(method);
            if (!expected.Contains(key) || !found.Add(key))
            {
                throw new InvalidOperationException($"Unexpected P/Invoke '{assembly.Name.Name}:{key}'.");
            }
        }

        if (!found.SetEquals(expected))
        {
            var missing = expected.Except(found, StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Known P/Invoke set changed in {assembly.Name.Name}: missing {string.Join(", ", missing)}.");
        }

        foreach (var method in methods.Where(method => method.PInvokeInfo is not null))
        {
            ReplaceWithPlatformNotSupported(method);
        }

        return found.Count != 0;
    }

    private static string ImportKey(MethodDefinition method)
    {
        var import = method.PInvokeInfo!;
        return $"{method.DeclaringType.FullName}::{method.Name}|{import.Module.Name}|{import.EntryPoint}";
    }

    private static void ReplaceWithPlatformNotSupported(MethodDefinition method)
    {
        method.PInvokeInfo = null;
        method.Attributes &= ~MethodAttributes.PInvokeImpl;
        method.ImplAttributes &= ~(MethodImplAttributes.CodeTypeMask |
                                   MethodImplAttributes.ManagedMask |
                                   MethodImplAttributes.PreserveSig);
        method.ImplAttributes |= MethodImplAttributes.IL | MethodImplAttributes.Managed;
        method.Body = new MethodBody(method);

        var module = method.Module;
        var exceptionType = new TypeReference(
            "System",
            nameof(PlatformNotSupportedException),
            module,
            module.TypeSystem.CoreLibrary);
        var constructor = new MethodReference(".ctor", module.TypeSystem.Void, exceptionType)
        {
            HasThis = true
        };
        constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var processor = method.Body.GetILProcessor();
        processor.Emit(OpCodes.Ldstr, UnsupportedMessage);
        processor.Emit(OpCodes.Newobj, constructor);
        processor.Emit(OpCodes.Throw);
    }

    private static bool StripStrongName(AssemblyDefinition assembly)
    {
        if (!StrippedPublicKeyTokens.TryGetValue(assembly.Name.Name, out var expectedToken))
        {
            return false;
        }

        var actualToken = TokenText(assembly.Name.PublicKeyToken);
        if (!string.Equals(actualToken, expectedToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected public key token for {assembly.Name.Name}: expected {expectedToken}, found {actualToken}.");
        }

        assembly.Name.PublicKey = Array.Empty<byte>();
        assembly.Name.Attributes &= ~AssemblyAttributes.PublicKey;
        assembly.MainModule.Attributes &= ~ModuleAttributes.StrongNameSigned;
        return true;
    }

    private static bool RewriteStrongAssemblyReferences(ModuleDefinition module)
    {
        var changed = false;
        foreach (var reference in module.AssemblyReferences)
        {
            if (!ManagedAssemblyVersions.TryGetValue(reference.Name, out var expectedVersion))
            {
                continue;
            }

            if (HostProvidedAssemblies.Contains(reference.Name))
            {
                if (reference.Version != expectedVersion)
                {
                    reference.Version = expectedVersion;
                    changed = true;
                }

                continue;
            }

            if (reference.Version != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Unexpected reference version for {reference.Name}: expected {expectedVersion}, found {reference.Version}.");
            }

            if (!StrippedPublicKeyTokens.TryGetValue(reference.Name, out var expectedToken))
            {
                continue;
            }

            var actualToken = TokenText(reference.PublicKeyToken);
            if (actualToken.Length != 0 && !string.Equals(actualToken, expectedToken, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected reference token for {reference.Name}: expected {expectedToken}, found {actualToken}.");
            }

            if (actualToken.Length != 0 || reference.HasPublicKey)
            {
                reference.PublicKey = Array.Empty<byte>();
                reference.PublicKeyToken = Array.Empty<byte>();
                reference.Attributes &= ~AssemblyAttributes.PublicKey;
                changed = true;
            }
        }

        return changed;
    }

    private static void ValidateSanitizedAssembly(
        string path,
        bool allowMediaAssembly,
        string? referenceDirectory = null)
    {
        using (var peStream = File.OpenRead(path))
        {
            StarlightAssemblyValidator.ValidateStrictPe(peStream, path);
        }

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        if (referenceDirectory is not null)
        {
            resolver.AddSearchDirectory(referenceDirectory);
        }
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false,
            AssemblyResolver = resolver,
        });
        ValidateAssemblyIdentity(assembly.Name, allowMediaAssembly);
        ValidateResources(assembly.MainModule);
        ValidateNoTmdsMetadata(assembly.MainModule);
        ValidateNoFunctionPointerSignatures(
            assembly.MainModule,
            $"Dynamic native interop remains in {assembly.Name.Name}");

        if (StrippedPublicKeyTokens.ContainsKey(assembly.Name.Name) &&
            (assembly.Name.HasPublicKey || assembly.Name.PublicKeyToken.Length != 0 ||
             (assembly.MainModule.Attributes & ModuleAttributes.StrongNameSigned) != 0))
        {
            throw new InvalidOperationException($"Strong name remains on {assembly.Name.Name}.");
        }

        foreach (var reference in assembly.MainModule.AssemblyReferences)
        {
            if (ManagedAssemblyVersions.TryGetValue(reference.Name, out var expectedVersion) &&
                reference.Version != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Unexpected reference version for {reference.Name}: expected {expectedVersion}, found {reference.Version}.");
            }

            if (StrippedPublicKeyTokens.ContainsKey(reference.Name) &&
                (reference.HasPublicKey || reference.PublicKeyToken.Length != 0))
            {
                throw new InvalidOperationException(
                    $"Strong assembly reference remains in {assembly.Name.Name}: {reference.FullName}.");
            }
        }

        foreach (var method in AllTypes(assembly.MainModule).SelectMany(static type => type.Methods))
        {
            var hasPInvokeAttribute = (method.Attributes & MethodAttributes.PInvokeImpl) != 0;
            var hasPInvokeInfo = method.PInvokeInfo is not null;
            if (hasPInvokeAttribute || hasPInvokeInfo)
            {
                throw new InvalidOperationException(
                    $"P/Invoke metadata remains in {assembly.Name.Name}: {method.FullName}; " +
                    $"MethodAttributes.PInvokeImpl={hasPInvokeAttribute}, PInvokeInfo={hasPInvokeInfo}.");
            }

            if (!method.HasBody)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                ValidateDynamicNativeInstruction(
                    instruction,
                    method,
                    $"Dynamic native interop remains in {assembly.Name.Name}");
            }
        }
    }

    private static void ValidateNoTmdsMetadata(ModuleDefinition module)
    {
        var assemblyReference = module.AssemblyReferences
            .FirstOrDefault(static reference => reference.Name == TmdsLibCAssemblyName);
        if (assemblyReference is not null)
        {
            throw new InvalidOperationException(
                $"{TmdsLibCAssemblyName} assembly reference remains: {assemblyReference.FullName}.");
        }

        var typeReference = module.GetTypeReferences().FirstOrDefault(IsTmdsType);
        if (typeReference is not null)
        {
            throw new InvalidOperationException(
                $"{TmdsLibCAssemblyName} type reference remains: {typeReference.FullName}.");
        }

        var memberReference = module.GetMemberReferences()
            .FirstOrDefault(static reference => IsTmdsType(reference.DeclaringType));
        if (memberReference is not null)
        {
            throw new InvalidOperationException(
                $"{TmdsLibCAssemblyName} member reference remains: {memberReference.FullName}.");
        }
    }

    private static bool IsTmdsType(TypeReference type)
    {
        while (type is TypeSpecification specification)
        {
            type = specification.ElementType;
        }

        return type.Scope is AssemblyNameReference assemblyReference &&
               string.Equals(assemblyReference.Name, TmdsLibCAssemblyName, StringComparison.Ordinal);
    }

    private static void ValidateNoFunctionPointerSignatures(ModuleDefinition module, string message)
    {
        foreach (var type in AllTypes(module))
        {
            if (type.Fields.Any(static field => ContainsFunctionPointer(field.FieldType)) ||
                type.Properties.Any(static property =>
                    ContainsFunctionPointer(property.PropertyType) ||
                    property.Parameters.Any(static parameter => ContainsFunctionPointer(parameter.ParameterType))) ||
                type.Events.Any(static @event => ContainsFunctionPointer(@event.EventType)) ||
                type.Methods.Any(static method =>
                    ContainsFunctionPointer(method.ReturnType) ||
                    method.Parameters.Any(static parameter => ContainsFunctionPointer(parameter.ParameterType)) ||
                    method.HasBody &&
                    method.Body.Variables.Any(static variable => ContainsFunctionPointer(variable.VariableType))))
            {
                throw new InvalidOperationException($"{message} a function-pointer signature in {type.FullName}.");
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

    private static void ValidateDynamicNativeInstruction(
        Instruction instruction,
        MethodDefinition containingMethod,
        string message)
    {
        if (instruction.OpCode == OpCodes.Calli)
        {
            throw new InvalidOperationException($"{message} via calli: {containingMethod.FullName}.");
        }

        if (instruction.Operand is not MethodReference calledMethod)
        {
            return;
        }

        if (string.Equals(calledMethod.DeclaringType.FullName, NativeLibraryTypeName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{message} via NativeLibrary: {containingMethod.FullName}.");
        }

        if (string.Equals(calledMethod.DeclaringType.FullName, MarshalTypeName, StringComparison.Ordinal) &&
            calledMethod.Name is "GetDelegateForFunctionPointer" or "GetFunctionPointerForDelegate")
        {
            throw new InvalidOperationException(
                $"{message} via Marshal delegate bridge: {containingMethod.FullName}.");
        }
    }

    private static void ValidateResources(ModuleDefinition module)
    {
        Span<byte> magic = stackalloc byte[8];

        foreach (var resource in module.Resources)
        {
            if (resource is not EmbeddedResource embedded)
            {
                throw new InvalidOperationException($"Linked assembly resource is prohibited: {resource.Name}.");
            }

            if (HasNativeExtension(resource.Name))
            {
                throw new InvalidOperationException($"Native assembly resource is prohibited: {resource.Name}.");
            }

            using var stream = embedded.GetResourceStream();
            var count = stream.Read(magic);
            if (IsNativeMagic(magic[..count]))
            {
                throw new InvalidOperationException($"Embedded native payload is prohibited: {resource.Name}.");
            }
            if (resource.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) ||
                IsManagedResourceMagic(magic[..count]))
            {
                stream.Position = 0;
                ValidateManagedResourceContainer(stream, resource.Name);
            }
        }
    }

    private static bool IsManagedResourceMagic(ReadOnlySpan<byte> magic)
    {
        return magic.StartsWith(new byte[] { 0xce, 0xca, 0xef, 0xbe });
    }

    private static void ValidateManagedResourceContainer(Stream stream, string resourceName)
    {
        try
        {
            using var reader = new System.Resources.ResourceReader(stream);
            var entries = reader.GetEnumerator();
            while (entries.MoveNext())
            {
                var entryName = entries.Key as string ??
                                throw new InvalidDataException("Resource entry name is not a string.");
                reader.GetResourceData(entryName, out var typeName, out var serialized);
                if (typeName is not "ResourceTypeCode.ByteArray" and not "ResourceTypeCode.Stream")
                {
                    continue;
                }

                if (serialized.Length < sizeof(int))
                {
                    throw new InvalidDataException($"Resource entry '{entryName}' has a truncated value.");
                }

                var length = BinaryPrimitives.ReadInt32LittleEndian(serialized);
                if (length < 0 || length != serialized.Length - sizeof(int))
                {
                    throw new InvalidDataException($"Resource entry '{entryName}' has an invalid length.");
                }

                var value = serialized.AsSpan(sizeof(int), length);
                if (HasNativeExtension(entryName) || IsNativeMagic(value[..Math.Min(value.Length, 8)]))
                {
                    throw new InvalidOperationException(
                        $"Managed resource container embeds a native payload: {resourceName}/{entryName}.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception error) when (
            error is ArgumentException or
            BadImageFormatException or
            EndOfStreamException or
            FormatException or
            IOException)
        {
            throw new InvalidOperationException(
                $"Managed resource container is invalid: {resourceName}.",
                error);
        }
    }

    private static bool HasNativeExtension(string name)
    {
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".so.", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".a", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNativeMagic(ReadOnlySpan<byte> magic)
    {
        return magic.StartsWith(new byte[] { (byte)'M', (byte)'Z' }) ||
               magic.StartsWith(new byte[] { (byte)'P', (byte)'K' }) ||
               magic.StartsWith(new byte[] { 0x1f, 0x8b }) ||
               magic.StartsWith(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }) ||
               magic.StartsWith(new byte[] { (byte)'!', (byte)'<', (byte)'a', (byte)'r', (byte)'c', (byte)'h', (byte)'>', (byte)'\n' }) ||
               magic.StartsWith(new byte[] { (byte)'7', (byte)'z', 0xbc, 0xaf, 0x27, 0x1c }) ||
               magic.StartsWith(new byte[] { 0xfe, 0xed, 0xfa, 0xce }) ||
               magic.StartsWith(new byte[] { 0xfe, 0xed, 0xfa, 0xcf }) ||
               magic.StartsWith(new byte[] { 0xce, 0xfa, 0xed, 0xfe }) ||
               magic.StartsWith(new byte[] { 0xcf, 0xfa, 0xed, 0xfe }) ||
               magic.StartsWith(new byte[] { 0xca, 0xfe, 0xba, 0xbe }) ||
               magic.StartsWith(new byte[] { 0xbe, 0xba, 0xfe, 0xca }) ||
               magic.StartsWith(new byte[] { 0xca, 0xfe, 0xba, 0xbf }) ||
               magic.StartsWith(new byte[] { 0xbf, 0xba, 0xfe, 0xca });
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            foreach (var nested in SelfAndNested(type))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<TypeDefinition> SelfAndNested(TypeDefinition type)
    {
        yield return type;
        foreach (var child in type.NestedTypes)
        {
            foreach (var nested in SelfAndNested(child))
            {
                yield return nested;
            }
        }
    }

    private static void ValidateInputDirectory(string directory, bool validateAssemblies)
    {
        directory = Path.GetFullPath(directory);
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            throw new InvalidOperationException("Managed input directory is empty.");
        }

        foreach (var file in files)
        {
            if (!string.Equals(Path.GetDirectoryName(file), directory, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Nested managed input is prohibited: {Path.GetFileName(file)}.");
            }

            if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Non-DLL managed input is prohibited: {Path.GetFileName(file)}.");
            }

            if (validateAssemblies)
            {
                ValidateSanitizedAssembly(
                    file,
                    string.Equals(
                        Path.GetFileNameWithoutExtension(file),
                        MediaAssemblyName,
                        StringComparison.Ordinal));
            }
        }

        var expectedNames = PinnedThirdPartyInputHashes.Keys
            .Select(static identity => identity.Name)
            .Append(MediaAssemblyName)
            .ToHashSet(StringComparer.Ordinal);
        var actualNames = files
            .Select(static path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedNames.SetEquals(actualNames))
        {
            throw new InvalidOperationException(
                $"Managed input closure mismatch. Expected [{string.Join(", ", expectedNames.Order())}], " +
                $"found [{string.Join(", ", actualNames.Order())}].");
        }
    }

    private static void Merge(
        string pluginPath,
        string inputDirectory,
        string referenceManifestPath,
        string outputPath)
    {
        pluginPath = Path.GetFullPath(pluginPath);
        inputDirectory = Path.GetFullPath(inputDirectory);
        referenceManifestPath = Path.GetFullPath(referenceManifestPath);
        outputPath = Path.GetFullPath(outputPath);
        RequireFile(pluginPath);
        RequireFile(referenceManifestPath);
        ValidateInputDirectory(inputDirectory, true);
        if (!string.Equals(Path.GetFileName(outputPath), OutputFileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Merged output must be named {OutputFileName}.");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var workingDirectory = Path.Combine(
            outputDirectory,
            $".perfectcomms-starlight-merge-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var sanitizedPrimaryPath = Path.Combine(workingDirectory, PluginAssemblyName + ".dll");
            SanitizePlugin(pluginPath, sanitizedPrimaryPath);
            var snapshot = ReadPrimarySnapshot(sanitizedPrimaryPath);
            var managedInputs = Directory.GetFiles(inputDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
            var orderedInputs = new[] { sanitizedPrimaryPath }.Concat(managedInputs).ToArray();
            ValidateManagedClosure(orderedInputs);
            var deterministicHash = HashOrderedInputs(orderedInputs);
            var repackedDirectory = Path.Combine(workingDirectory, "repacked");
            Directory.CreateDirectory(repackedDirectory);
            var repackedPath = Path.Combine(repackedDirectory, PluginAssemblyName + ".dll");
            var searchDirectories = ReadReferenceDirectories(referenceManifestPath)
                .Append(Path.GetDirectoryName(pluginPath)!)
                .Append(inputDirectory)
                .Append(workingDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            var options = new RepackOptions
            {
                InputAssemblies = orderedInputs,
                OutputFile = repackedPath,
                SearchDirectories = searchDirectories,
                TargetKind = ILRepack.Kind.Dll,
                Version = PluginVersion,
                Internalize = true,
                DebugInfo = false,
                NoRepackRes = true,
                CopyAttributes = false,
                AllowMultipleAssemblyLevelAttributes = false,
                AllowDuplicateResources = false,
                AllowZeroPeKind = false,
                KeepOtherVersionReferences = false,
                Parallel = false,
                PreserveTimestamp = false,
                SkipConfigMerge = true,
                XmlDocumentation = false
            };
            new ILRepack(options).Repack();

            var normalizedPath = Path.Combine(workingDirectory, OutputFileName);
            NormalizeMergedAssembly(
                repackedPath,
                normalizedPath,
                snapshot,
                deterministicHash,
                searchDirectories);
            ValidateMergedAssembly(
                normalizedPath,
                snapshot,
                DeterministicGuid(deterministicHash));
            File.Move(normalizedPath, outputPath, true);
            ValidateMergedAssembly(outputPath);
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, true);
            }
        }
    }

    private static string[] ReadReferenceDirectories(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var references = document.RootElement
            .GetProperty("Items")
            .GetProperty("ReferencePath");
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references.EnumerateArray())
        {
            var path = reference.TryGetProperty("FullPath", out var fullPath)
                ? fullPath.GetString()
                : reference.GetProperty("Identity").GetString();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("ReferencePath contains an empty path.");
            }

            path = Path.GetFullPath(path);
            RequireFile(path);
            directories.Add(Path.GetDirectoryName(path)!);
        }

        if (directories.Count == 0)
        {
            throw new InvalidOperationException("ReferencePath contains no managed reference directories.");
        }

        return directories.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateManagedClosure(IReadOnlyCollection<string> inputPaths)
    {
        var names = inputPaths
            .Select(ReadAssemblyName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in inputPaths)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = false
            });
            foreach (var reference in assembly.MainModule.AssemblyReferences)
            {
                if (ManagedAssemblyVersions.ContainsKey(reference.Name) &&
                    !HostProvidedAssemblies.Contains(reference.Name) &&
                    !names.Contains(reference.Name))
                {
                    throw new InvalidOperationException(
                        $"Managed input closure is missing {reference.Name}, referenced by {assembly.Name.Name}.");
                }
            }
        }
    }

    private static string ReadAssemblyName(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false
        });
        return assembly.Name.Name;
    }

    private static byte[] HashOrderedInputs(IEnumerable<string> inputPaths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in inputPaths)
        {
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(path)));
        }

        return hash.GetHashAndReset();
    }
    private sealed record PrimarySnapshot(
        string Name,
        Version Version,
        string Culture,
        AssemblyAttributes Attributes,
        AssemblyHashAlgorithm HashAlgorithm,
        byte[] PublicKey,
        string ModuleName,
        string[] CustomAttributes,
        IReadOnlyDictionary<string, string> Resources,
        string[] PublicTypes);

    private static PrimarySnapshot ReadPrimarySnapshot(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false
        });
        return new PrimarySnapshot(
            assembly.Name.Name,
            assembly.Name.Version,
            assembly.Name.Culture,
            assembly.Name.Attributes,
            assembly.Name.HashAlgorithm,
            assembly.Name.PublicKey.ToArray(),
            assembly.MainModule.Name,
            assembly.CustomAttributes.Select(AttributeKey).Order().ToArray(),
            ResourceHashes(assembly.MainModule),
            ExternallyVisibleTypes(assembly.MainModule));
    }

    private static void NormalizeMergedAssembly(
        string inputPath,
        string outputPath,
        PrimarySnapshot snapshot,
        byte[] deterministicHash,
        IEnumerable<string> searchDirectories)
    {
        using var resolver = new DefaultAssemblyResolver();
        foreach (var directory in searchDirectories)
        {
            resolver.AddSearchDirectory(directory);
        }

        using var assembly = AssemblyDefinition.ReadAssembly(inputPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadSymbols = false
        });
        assembly.Name.Name = snapshot.Name;
        assembly.Name.Version = snapshot.Version;
        assembly.Name.Culture = snapshot.Culture;
        assembly.Name.Attributes = snapshot.Attributes;
        assembly.Name.HashAlgorithm = snapshot.HashAlgorithm;
        assembly.Name.PublicKey = snapshot.PublicKey.ToArray();
        assembly.MainModule.Name = snapshot.ModuleName;
        assembly.MainModule.Mvid = DeterministicGuid(deterministicHash);
        if (snapshot.PublicKey.Length == 0)
        {
            assembly.MainModule.Attributes &= ~ModuleAttributes.StrongNameSigned;
        }
        RemoveUnusedModuleReferences(assembly.MainModule);

        File.Delete(outputPath);
        assembly.Write(outputPath, new WriterParameters
        {
            WriteSymbols = false,
            Timestamp = DeterministicTimestamp(deterministicHash)
        });
    }

    private static void RemoveUnusedModuleReferences(ModuleDefinition module)
    {
        var used = AllTypes(module)
            .SelectMany(static type => type.Methods)
            .Where(static method => method.PInvokeInfo is not null)
            .Select(static method => method.PInvokeInfo!.Module)
            .ToHashSet();
        for (var index = module.ModuleReferences.Count - 1; index >= 0; index--)
        {
            if (!used.Contains(module.ModuleReferences[index]))
            {
                module.ModuleReferences.RemoveAt(index);
            }
        }
    }

    private static Guid DeterministicGuid(byte[] hash)
    {
        var bytes = hash[..16].ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private static uint DeterministicTimestamp(byte[] hash)
    {
        return (uint)(hash[16] |
                      hash[17] << 8 |
                      hash[18] << 16 |
                      hash[19] << 24);
    }

    private static string AttributeKey(CustomAttribute attribute)
    {
        return attribute.AttributeType.FullName + ":" + Convert.ToHexString(attribute.GetBlob());
    }

    private static IReadOnlyDictionary<string, string> ResourceHashes(ModuleDefinition module)
    {
        return module.Resources.ToDictionary(
            static resource => resource.Name,
            static resource =>
            {
                if (resource is not EmbeddedResource embedded)
                {
                    throw new InvalidOperationException($"Linked assembly resource is prohibited: {resource.Name}.");
                }

                using var stream = embedded.GetResourceStream();
                return Convert.ToHexString(SHA256.HashData(stream));
            },
            StringComparer.Ordinal);
    }

    private static string[] ExternallyVisibleTypes(ModuleDefinition module)
    {
        return AllTypes(module)
            .Where(IsExternallyVisible)
            .Select(static type => type.FullName)
            .Order()
            .ToArray();
    }

    private static bool IsExternallyVisible(TypeDefinition type)
    {
        if (type.DeclaringType is null)
        {
            return type.IsPublic;
        }

        return type.IsNestedPublic && IsExternallyVisible(type.DeclaringType);
    }

    private static void ValidatePrimaryPreservation(ModuleDefinition module, PrimarySnapshot snapshot)
    {
        var assembly = module.Assembly;
        if (!string.Equals(assembly.Name.Name, snapshot.Name, StringComparison.Ordinal) ||
            assembly.Name.Version != snapshot.Version ||
            !string.Equals(assembly.Name.Culture, snapshot.Culture, StringComparison.Ordinal) ||
            assembly.Name.Attributes != snapshot.Attributes ||
            assembly.Name.HashAlgorithm != snapshot.HashAlgorithm ||
            !assembly.Name.PublicKey.SequenceEqual(snapshot.PublicKey) ||
            !string.Equals(module.Name, snapshot.ModuleName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Merged output did not preserve the primary assembly identity.");
        }

        if (!assembly.CustomAttributes.Select(AttributeKey).Order().SequenceEqual(snapshot.CustomAttributes))
        {
            throw new InvalidOperationException("Merged output did not preserve primary assembly attributes.");
        }

        var resources = ResourceHashes(module);
        foreach (var resource in snapshot.Resources)
        {
            if (!resources.TryGetValue(resource.Key, out var hash) ||
                !string.Equals(hash, resource.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Merged output did not preserve primary resource '{resource.Key}'.");
            }
        }

        if (!ExternallyVisibleTypes(module).SequenceEqual(snapshot.PublicTypes))
        {
            throw new InvalidOperationException(
                "Merged output changed the primary public type surface or exposed dependency types.");
        }
    }

    private static void SanitizePlugin(string inputPath, string outputPath)
    {
        var inputBytes = File.ReadAllBytes(inputPath);
        using (var peStream = new MemoryStream(inputBytes, false))
        {
            StarlightAssemblyValidator.ValidateStrictPe(peStream, inputPath);
        }

        using var assemblyStream = new MemoryStream(inputBytes, false);
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyStream, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false
        });
        ValidatePluginIdentity(assembly.Name);
        ValidateResources(assembly.MainModule);
        ValidateNoticeResources(assembly.MainModule);
        ValidatePluginNativeInterop(assembly.MainModule, "The Starlight plugin contains");

        var changed = RewriteStrongAssemblyReferences(assembly.MainModule);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (!changed)
        {
            File.WriteAllBytes(outputPath, inputBytes);
        }
        else
        {
            var sourceHash = SHA256.HashData(inputBytes);
            assembly.MainModule.Mvid = DeterministicGuid(sourceHash);
            var temporaryPath = outputPath + ".tmp";
            File.Delete(temporaryPath);
            assembly.Write(temporaryPath, new WriterParameters
            {
                WriteSymbols = false,
                Timestamp = DeterministicTimestamp(sourceHash)
            });
            File.Move(temporaryPath, outputPath, true);
        }

        ValidatePluginAssembly(outputPath);
    }

    private static void ValidatePluginAssembly(string path)
    {
        using (var peStream = File.OpenRead(path))
        {
            StarlightAssemblyValidator.ValidateStrictPe(peStream, path);
        }

        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false
        });
        ValidatePluginIdentity(assembly.Name);
        ValidateResources(assembly.MainModule);
        ValidateNoticeResources(assembly.MainModule);
        ValidateEmptyDependencyMetadata(assembly.MainModule);
        foreach (var reference in assembly.MainModule.AssemblyReferences)
        {
            if (ManagedAssemblyVersions.TryGetValue(reference.Name, out var expectedVersion) &&
                reference.Version != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Unexpected plugin reference version for {reference.Name}: expected {expectedVersion}, found {reference.Version}.");
            }

            if (StrippedPublicKeyTokens.ContainsKey(reference.Name) &&
                (reference.HasPublicKey || reference.PublicKeyToken.Length != 0))
            {
                throw new InvalidOperationException($"Strong reference remains in plugin: {reference.FullName}.");
            }
        }

        ValidatePluginNativeInterop(assembly.MainModule, "The sanitized Starlight plugin contains");
    }

    private static void ValidatePluginIdentity(AssemblyNameDefinition name)
    {
        if (!string.Equals(name.Name, PluginAssemblyName, StringComparison.Ordinal) ||
            name.Version != PluginVersion)
        {
            throw new InvalidOperationException(
                $"Unexpected Starlight plugin assembly identity '{name.FullName}'. Expected {PluginAssemblyName}, Version={PluginVersion}.");
        }
    }

    private static void ValidatePluginNativeInterop(ModuleDefinition module, string message)
    {
        ValidateNoFunctionPointerSignatures(module, message);
        var usedModuleReferences = new HashSet<ModuleReference>();
        foreach (var method in AllTypes(module).SelectMany(static type => type.Methods))
        {
            var hasPInvokeAttribute = (method.Attributes & MethodAttributes.PInvokeImpl) != 0;
            var hasPInvokeInfo = method.PInvokeInfo is not null;
            if (hasPInvokeAttribute != hasPInvokeInfo)
            {
                throw new InvalidOperationException($"{message} malformed P/Invoke metadata: {method.FullName}.");
            }

            if (method.PInvokeInfo is not null)
            {
                if (!StarlightAssemblyValidator.IsApprovedCaptureImport(method))
                {
                    throw new InvalidOperationException(
                        $"{message} an unexpected P/Invoke ABI: {method.FullName}.");
                }

                usedModuleReferences.Add(method.PInvokeInfo.Module);
            }

            if (!method.HasBody)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                ValidateDynamicNativeInstruction(instruction, method, message);
            }
        }

        if (!StarlightAssemblyValidator.HasExactApprovedCaptureImports(module))
        {
            throw new InvalidOperationException(
                $"{message} a missing or duplicate approved capture import.");
        }

        foreach (var moduleReference in module.ModuleReferences)
        {
            if (!string.Equals(
                    moduleReference.Name,
                    StarlightNativeLibraryName,
                    StringComparison.Ordinal) ||
                !usedModuleReferences.Contains(moduleReference))
            {
                throw new InvalidOperationException(
                    $"{message} an unexpected native module reference: {moduleReference.Name}.");
            }
        }
    }

    private static IReadOnlySet<string> ValidateExternalAssemblyReferences(ModuleDefinition module)
    {
        var mergedNames = PinnedThirdPartyInputHashes.Keys
            .Select(static identity => identity.Name)
            .Append(MediaAssemblyName)
            .ToHashSet(StringComparer.Ordinal);
        var hostReferences = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in module.AssemblyReferences)
        {
            if (mergedNames.Contains(reference.Name))
            {
                throw new InvalidOperationException(
                    $"Merged input assembly reference remains: {reference.FullName}.");
            }

            if (!string.IsNullOrEmpty(reference.Culture))
            {
                throw new InvalidOperationException(
                    $"External assembly reference must have neutral culture: {reference.FullName}.");
            }
            if (reference.Attributes != 0)
            {
                throw new InvalidOperationException(
                    $"External assembly reference has prohibited flags {reference.Attributes}: {reference.FullName}.");
            }


            var identity = $"{reference.Name}|{reference.Version}|{TokenText(reference.PublicKeyToken)}";
            if (!AllowedExternalAssemblyReferences.Contains(identity))
            {
                throw new InvalidOperationException(
                    $"External assembly reference is outside the Starlight 1.6.3 contract: {reference.FullName}.");
            }

            if (HostProvidedAssemblies.Contains(reference.Name))
            {
                hostReferences.Add(reference.Name);
            }
        }

        return hostReferences;
    }

    private static void ValidateMergedAssembly(
        string path,
        PrimarySnapshot? snapshot = null,
        Guid? expectedMvid = null)
    {
        path = Path.GetFullPath(path);
        RequireFile(path);
        if (!string.Equals(Path.GetFileName(path), OutputFileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Merged assembly must be named {OutputFileName}.");
        }

        var exactPassed = StarlightAssemblyValidator.IsAssemblySafe(path);
        if (!exactPassed)
        {
            throw new InvalidOperationException("Exact Starlight assembly validation failed.");
        }

        using (var peStream = File.OpenRead(path))
        {
            StarlightAssemblyValidator.ValidateStrictPe(peStream, path);
            peStream.Position = 0;
            using var peReader = new PEReader(peStream, PEStreamOptions.LeaveOpen);
            if (peReader.ReadDebugDirectory().Length != 0)
            {
                throw new InvalidOperationException("Merged Starlight assembly contains debug metadata.");
            }
        }

        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false
        });
        ValidatePluginIdentity(assembly.Name);
        if (assembly.Name.HasPublicKey ||
            assembly.Name.PublicKeyToken.Length != 0 ||
            (assembly.MainModule.Attributes & ModuleAttributes.StrongNameSigned) != 0)
        {
            throw new InvalidOperationException("Merged Starlight assembly must be unsigned.");
        }
        ValidateResources(assembly.MainModule);
        ValidateNoticeResources(assembly.MainModule);
        ValidatePluginNativeInterop(assembly.MainModule, "The merged Starlight plugin contains");
        ValidateEmptyDependencyMetadata(assembly.MainModule);
        ValidateNoTmdsMetadata(assembly.MainModule);

        var hostReferences = ValidateExternalAssemblyReferences(assembly.MainModule);

        if (!hostReferences.Contains(RequiredHostReference))
        {
            throw new InvalidOperationException(
                $"Merged output must retain the host-provided {RequiredHostReference} reference.");
        }

        if (assembly.MainModule.Resources.Any(static resource =>
                resource.Name.Contains("ILRepack", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("ILRepack marker resource remains in merged output.");
        }

        if (expectedMvid is not null && assembly.MainModule.Mvid != expectedMvid.Value)
        {
            throw new InvalidOperationException("Merged output MVID is not derived from its ordered input hashes.");
        }
        if (snapshot is not null)
        {
            ValidatePrimaryPreservation(assembly.MainModule, snapshot);
        }

        Console.WriteLine(
            $"starlight.validation.file file={Path.GetFileName(path)} exact=pass strict-pe=pass strict=pass");
        Console.WriteLine("starlight.validation.ok files=1");
    }

    private static void ValidateNoticeResources(ModuleDefinition module)
    {
        var resources = module.Resources.ToDictionary(static resource => resource.Name, StringComparer.Ordinal);
        foreach (var expected in RequiredNoticeResourceHashes)
        {
            if (!resources.TryGetValue(expected.Key, out var resource) ||
                resource is not EmbeddedResource embedded)
            {
                throw new InvalidOperationException($"Required embedded notice is missing: {expected.Key}.");
            }


            using var stream = embedded.GetResourceStream();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            if (bytes.Length == 0)
            {
                throw new InvalidOperationException($"Required embedded notice is empty: {expected.Key}.");
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, expected.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Required embedded notice content does not match its pinned source: {expected.Key}.");
            }

            try
            {
                _ = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidOperationException(
                    $"Required embedded notice is not valid UTF-8: {expected.Key}.",
                    exception);
            }
        }
    }

    private static void ValidateEmptyDependencyMetadata(ModuleDefinition module)
    {
        var dependencyTypes = AllTypes(module)
            .Where(static type => type.FullName == "Starlight.Dependencies")
            .ToArray();
        if (dependencyTypes.Length != 1)
        {
            throw new InvalidOperationException(
                "PerfectCommsStarlight.dll must contain exactly one Starlight.Dependencies type.");
        }

        var dependencyType = dependencyTypes[0];
        var fields = dependencyType.Fields.Where(static field => field.Name == "Files").ToArray();
        if (fields.Length != 1 ||
            !fields[0].IsStatic ||
            fields[0].FieldType.FullName != "System.String[][]")
        {
            throw new InvalidOperationException(
                "Starlight.Dependencies must contain exactly one static string[][] Files field.");
        }

        var field = fields[0];

        var initializer = dependencyType.Methods.SingleOrDefault(
            static method => method.IsConstructor && method.IsStatic);
        if (initializer is null ||
            EvaluateMetadataArray(initializer, field) is not object?[] { Length: 0 })
        {
            throw new InvalidOperationException("Starlight.Dependencies.Files must be empty.");
        }
    }

    private static object? EvaluateMetadataArray(MethodDefinition method, FieldDefinition? targetField)
    {
        if (!method.HasBody)
        {
            throw new InvalidOperationException("Starlight dependency metadata initializer has no body.");
        }

        var stack = new List<object?>();
        object? storedValue = null;
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Nop)
            {
                continue;
            }

            if (TryReadInt32(instruction, out var integer))
            {
                stack.Add(integer);
                continue;
            }
            if (instruction.OpCode == OpCodes.Call &&
                instruction.Operand is GenericInstanceMethod emptyCall &&
                emptyCall.Name == "Empty" &&
                emptyCall.Parameters.Count == 0 &&
                emptyCall.DeclaringType.FullName == "System.Array" &&
                emptyCall.GenericArguments.Count == 1 &&
                emptyCall.GenericArguments[0].FullName == "System.String[]")
            {
                stack.Add(Array.Empty<object?>());
                continue;
            }

            if (instruction.OpCode == OpCodes.Newarr)
            {
                var count = PopMetadataValue<int>(stack);
                if (count < 0)
                {
                    throw new InvalidOperationException("Starlight dependency metadata has a negative array length.");
                }

                stack.Add(new object?[count]);
                continue;
            }

            if (instruction.OpCode == OpCodes.Dup)
            {
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException("Starlight dependency metadata has an invalid stack.");
                }

                stack.Add(stack[^1]);
                continue;
            }

            if (instruction.OpCode == OpCodes.Ldstr)
            {
                stack.Add((string)instruction.Operand);
                continue;
            }

            if (instruction.OpCode == OpCodes.Stelem_Ref)
            {
                var element = PopMetadataValue<object>(stack);
                var index = PopMetadataValue<int>(stack);
                var array = PopMetadataValue<object?[]>(stack);
                if ((uint)index >= (uint)array.Length)
                {
                    throw new InvalidOperationException("Starlight dependency metadata has an invalid array index.");
                }

                array[index] = element;
                continue;
            }

            if (instruction.OpCode == OpCodes.Stsfld &&
                instruction.Operand is FieldReference storedField &&
                targetField is not null &&
                storedField.FullName == targetField.FullName)
            {
                storedValue = PopMetadataValue<object?[]>(stack);
                continue;
            }

            if (instruction.OpCode == OpCodes.Ret)
            {
                if (targetField is null)
                {
                    return PopMetadataValue<object?[]>(stack);
                }

                if (stack.Count != 0)
                {
                    throw new InvalidOperationException(
                        "Starlight dependency metadata initializer left values on the stack.");
                }

                return storedValue;
            }

            throw new InvalidOperationException(
                $"Starlight dependency metadata contains unsupported instruction '{instruction.OpCode}'.");
        }

        throw new InvalidOperationException("Starlight dependency metadata initializer did not return.");
    }

    private static T PopMetadataValue<T>(List<object?> stack)
    {
        if (stack.Count == 0)
        {
            throw new InvalidOperationException("Starlight dependency metadata has an invalid stack.");
        }

        var index = stack.Count - 1;
        var value = stack[index];
        stack.RemoveAt(index);
        if (value is not T typedValue)
        {
            throw new InvalidOperationException("Starlight dependency metadata has an invalid value.");
        }

        return typedValue;
    }

    private static bool TryReadInt32(Instruction instruction, out int value)
    {
        if (instruction.OpCode == OpCodes.Ldc_I4)
        {
            value = (int)instruction.Operand;
            return true;
        }

        if (instruction.OpCode == OpCodes.Ldc_I4_S)
        {
            value = (sbyte)instruction.Operand;
            return true;
        }

        value = instruction.OpCode.Code switch
        {
            Code.Ldc_I4_M1 => -1,
            Code.Ldc_I4_0 => 0,
            Code.Ldc_I4_1 => 1,
            Code.Ldc_I4_2 => 2,
            Code.Ldc_I4_3 => 3,
            Code.Ldc_I4_4 => 4,
            Code.Ldc_I4_5 => 5,
            Code.Ldc_I4_6 => 6,
            Code.Ldc_I4_7 => 7,
            Code.Ldc_I4_8 => 8,
            _ => 0
        };
        return instruction.OpCode.Code is Code.Ldc_I4_M1 or
            Code.Ldc_I4_0 or
            Code.Ldc_I4_1 or
            Code.Ldc_I4_2 or
            Code.Ldc_I4_3 or
            Code.Ldc_I4_4 or
            Code.Ldc_I4_5 or
            Code.Ldc_I4_6 or
            Code.Ldc_I4_7 or
            Code.Ldc_I4_8;
    }


    private static Dictionary<string, List<string>> ParseOptions(ReadOnlySpan<string> args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new InvalidOperationException($"Invalid command option '{option}'.");
            }

            var name = option[2..];
            if (!options.TryGetValue(name, out var values))
            {
                values = new List<string>();
                options.Add(name, values);
            }

            values.Add(args[index + 1]);
        }

        return options;
    }

    private static string RequiredOption(IReadOnlyDictionary<string, List<string>> options, string name)
    {
        var values = RequiredOptions(options, name);
        if (values.Count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one --{name} option.");
        }

        return values[0];
    }

    private static IReadOnlyList<string> RequiredOptions(
        IReadOnlyDictionary<string, List<string>> options,
        string name)
    {
        if (!options.TryGetValue(name, out var values) || values.Count == 0)
        {
            throw new InvalidOperationException($"Missing --{name} option.");
        }

        return values;
    }

    private static IReadOnlySet<string> ImportSet(params string[] values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    private static string TokenText(byte[]? token)
    {
        return token is null ? string.Empty : Convert.ToHexString(token).ToLowerInvariant();
    }


    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        Directory.CreateDirectory(path);
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"Required file is missing or empty: {path}.");
        }
    }

}
