using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using WinPanX.Core.Contracts;

namespace WinPanX.Agent.Audio;

internal sealed class AudioPolicyRouter : IRouter
{
    // Undocumented WinRT activation class used by the Windows shell/Settings audio routing stack.
    // We keep two known interface variants because GUIDs differ across Windows releases.
    private const string AudioPolicyActivationClass = "Windows.Media.Internal.AudioPolicyConfig";
    private const string MmdevapiToken = @"\\?\SWD#MMDEVAPI#";
    private const string DevinterfaceAudioRender = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private readonly object _sync = new();
    private readonly IAudioPolicyConfigFactoryBridge? _factory;
    private readonly Dictionary<(AppRuntimeId AppId, RoutingRoles Role), string> _routeCache = [];

    public AudioPolicyRouter()
    {
        _factory = TryCreateFactory();
    }

    public bool IsSupported => _factory is not null;

    public Task<RoutingResult> BindToEndpointAsync(
        AppRuntimeId appId,
        string endpointId,
        RoutingRoles roles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_factory is null)
        {
            return Task.FromResult(CreateNotSupported(appId, endpointId, roles));
        }

        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return Task.FromResult(new RoutingResult(
                appId,
                endpointId,
                RoutingStatus.Failed,
                [new RoleRoutingResult(RoutingRoles.None, false, "INVALID_ENDPOINT", "Endpoint ID is empty.")],
                DateTime.UtcNow));
        }

        var packedDeviceId = PackDeviceId(endpointId);
        using var deviceIdHstring = HString.Create(packedDeviceId);

        var roleResults = new List<RoleRoutingResult>();
        foreach (var role in ExpandRoles(roles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roleKey = (appId, role);

            lock (_sync)
            {
                if (_routeCache.TryGetValue(roleKey, out var cachedEndpointId)
                    && string.Equals(cachedEndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                {
                    roleResults.Add(new RoleRoutingResult(role, true, null, "No-op; already routed."));
                    continue;
                }
            }

            var hr = _factory.SetPersistedDefaultAudioEndpoint(
                appId.ProcessId,
                DataFlow.Render,
                ToRole(role),
                deviceIdHstring.DangerousGetHandle());

            if (hr == 0)
            {
                lock (_sync)
                {
                    _routeCache[roleKey] = endpointId;
                }

                roleResults.Add(new RoleRoutingResult(role, true, null, null));
            }
            else
            {
                roleResults.Add(new RoleRoutingResult(
                    role,
                    false,
                    $"0x{hr:X8}",
                    "SetPersistedDefaultAudioEndpoint failed."));
            }
        }

        return Task.FromResult(CreateResult(appId, endpointId, roleResults));
    }

    public Task<RoutingResult> ResetToSystemDefaultAsync(
        AppRuntimeId appId,
        RoutingRoles roles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_factory is null)
        {
            return Task.FromResult(CreateNotSupported(appId, "default", roles));
        }

        var roleResults = new List<RoleRoutingResult>();
        foreach (var role in ExpandRoles(roles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hr = _factory.SetPersistedDefaultAudioEndpoint(
                appId.ProcessId,
                DataFlow.Render,
                ToRole(role),
                IntPtr.Zero);

            if (hr == 0)
            {
                lock (_sync)
                {
                    _routeCache.Remove((appId, role));
                }

                roleResults.Add(new RoleRoutingResult(role, true, null, null));
            }
            else
            {
                roleResults.Add(new RoleRoutingResult(
                    role,
                    false,
                    $"0x{hr:X8}",
                    "Reset call failed (best-effort reset via null HSTRING)."));
            }
        }

        return Task.FromResult(CreateResult(appId, "default", roleResults));
    }

    private static RoutingResult CreateNotSupported(AppRuntimeId appId, string endpointId, RoutingRoles roles)
    {
        var roleResults = ExpandRoles(roles)
            .Select(role => new RoleRoutingResult(
                role,
                false,
                "NOT_SUPPORTED",
                "Audio policy factory unavailable on this Windows build."))
            .ToArray();

        return new RoutingResult(
            appId,
            endpointId,
            RoutingStatus.NotSupported,
            roleResults,
            DateTime.UtcNow);
    }

    private static RoutingResult CreateResult(
        AppRuntimeId appId,
        string endpointId,
        IReadOnlyList<RoleRoutingResult> roleResults)
    {
        var succeededCount = roleResults.Count(r => r.Succeeded);
        var status = succeededCount switch
        {
            0 => RoutingStatus.Failed,
            var c when c == roleResults.Count => RoutingStatus.Success,
            _ => RoutingStatus.PartialSuccess
        };

        return new RoutingResult(appId, endpointId, status, roleResults, DateTime.UtcNow);
    }

    private static IReadOnlyList<RoutingRoles> ExpandRoles(RoutingRoles roles)
    {
        var result = new List<RoutingRoles>(3);
        if (roles.HasFlag(RoutingRoles.Console))
        {
            result.Add(RoutingRoles.Console);
        }

        if (roles.HasFlag(RoutingRoles.Multimedia))
        {
            result.Add(RoutingRoles.Multimedia);
        }

        if (roles.HasFlag(RoutingRoles.Communications))
        {
            result.Add(RoutingRoles.Communications);
        }

        return result.Count == 0 ? [RoutingRoles.Multimedia] : result;
    }

    private static Role ToRole(RoutingRoles role)
    {
        return role switch
        {
            RoutingRoles.Console => Role.Console,
            RoutingRoles.Multimedia => Role.Multimedia,
            RoutingRoles.Communications => Role.Communications,
            _ => Role.Multimedia
        };
    }

    private static string PackDeviceId(string endpointId)
    {
        // The policy API expects the SWD/MMDEVAPI packed render path, not the raw endpoint ID.
        return $"{MmdevapiToken}{endpointId}{DevinterfaceAudioRender}";
    }

    private static IAudioPolicyConfigFactoryBridge? TryCreateFactory()
    {
        if (TryCreateFactory<IAudioPolicyConfigFactoryVariantFor21H2>(out var modern))
        {
            return new FactoryBridge21H2(modern);
        }

        if (TryCreateFactory<IAudioPolicyConfigFactoryVariantForDownlevel>(out var downlevel))
        {
            return new FactoryBridgeDownlevel(downlevel);
        }

        return null;
    }

    private static bool TryCreateFactory<TFactory>(out TFactory factory)
        where TFactory : class
    {
        factory = default!;
        var iid = typeof(TFactory).GUID;
        // This activation class/interface pair is undocumented and may differ across builds.
        using var classId = HString.Create(AudioPolicyActivationClass);
        var hr = RoGetActivationFactory(classId.DangerousGetHandle(), ref iid, out var factoryPtr);
        if (hr != 0 || factoryPtr == IntPtr.Zero)
        {
            return false;
        }

        object? obj = null;
        try
        {
            obj = Marshal.GetObjectForIUnknown(factoryPtr);
            if (obj is not TFactory typed)
            {
                return false;
            }

            factory = typed;
            return true;
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero)
            {
                Marshal.Release(factoryPtr);
            }
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    private interface IAudioPolicyConfigFactoryBridge
    {
        uint SetPersistedDefaultAudioEndpoint(int processId, DataFlow flow, Role role, IntPtr deviceIdHstring);
    }

    private sealed class FactoryBridge21H2 : IAudioPolicyConfigFactoryBridge
    {
        private readonly IAudioPolicyConfigFactoryVariantFor21H2 _factory;

        public FactoryBridge21H2(IAudioPolicyConfigFactoryVariantFor21H2 factory)
        {
            _factory = factory;
        }

        public uint SetPersistedDefaultAudioEndpoint(int processId, DataFlow flow, Role role, IntPtr deviceIdHstring)
        {
            return _factory.SetPersistedDefaultAudioEndpoint(processId, flow, role, deviceIdHstring);
        }
    }

    private sealed class FactoryBridgeDownlevel : IAudioPolicyConfigFactoryBridge
    {
        private readonly IAudioPolicyConfigFactoryVariantForDownlevel _factory;

        public FactoryBridgeDownlevel(IAudioPolicyConfigFactoryVariantForDownlevel factory)
        {
            _factory = factory;
        }

        public uint SetPersistedDefaultAudioEndpoint(int processId, DataFlow flow, Role role, IntPtr deviceIdHstring)
        {
            return _factory.SetPersistedDefaultAudioEndpoint(processId, flow, role, deviceIdHstring);
        }
    }

    [Guid("ab3d4648-e242-459f-b02f-541c70306324")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactoryVariantFor21H2
    {
        [PreserveSig] int __inspectable__GetIids(out uint iidCount, out IntPtr iids);
        [PreserveSig] int __inspectable__GetRuntimeClassName(out IntPtr className);
        [PreserveSig] int __inspectable__GetTrustLevel(out int trustLevel);

        [PreserveSig] int __incomplete__add_CtxVolumeChange();
        [PreserveSig] int __incomplete__remove_CtxVolumeChanged();
        [PreserveSig] int __incomplete__add_RingerVibrateStateChanged();
        [PreserveSig] int __incomplete__remove_RingerVibrateStateChange();
        [PreserveSig] int __incomplete__SetVolumeGroupGainForId();
        [PreserveSig] int __incomplete__GetVolumeGroupGainForId();
        [PreserveSig] int __incomplete__GetActiveVolumeGroupForEndpointId();
        [PreserveSig] int __incomplete__GetVolumeGroupsForEndpoint();
        [PreserveSig] int __incomplete__GetCurrentVolumeContext();
        [PreserveSig] int __incomplete__SetVolumeGroupMuteForId();
        [PreserveSig] int __incomplete__GetVolumeGroupMuteForId();
        [PreserveSig] int __incomplete__SetRingerVibrateState();
        [PreserveSig] int __incomplete__GetRingerVibrateState();
        [PreserveSig] int __incomplete__SetPreferredChatApplication();
        [PreserveSig] int __incomplete__ResetPreferredChatApplication();
        [PreserveSig] int __incomplete__GetPreferredChatApplication();
        [PreserveSig] int __incomplete__GetCurrentChatApplications();
        [PreserveSig] int __incomplete__add_ChatContextChanged();
        [PreserveSig] int __incomplete__remove_ChatContextChanged();

        [PreserveSig]
        uint SetPersistedDefaultAudioEndpoint(int processId, DataFlow flow, Role role, IntPtr deviceId);

        [PreserveSig]
        uint GetPersistedDefaultAudioEndpoint(
            int processId,
            DataFlow flow,
            Role role,
            [Out, MarshalAs(UnmanagedType.HString)] out string deviceId);

        [PreserveSig]
        uint ClearAllPersistedApplicationDefaultEndpoints();
    }

    [Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactoryVariantForDownlevel
    {
        [PreserveSig] int __inspectable__GetIids(out uint iidCount, out IntPtr iids);
        [PreserveSig] int __inspectable__GetRuntimeClassName(out IntPtr className);
        [PreserveSig] int __inspectable__GetTrustLevel(out int trustLevel);

        [PreserveSig] int __incomplete__add_CtxVolumeChange();
        [PreserveSig] int __incomplete__remove_CtxVolumeChanged();
        [PreserveSig] int __incomplete__add_RingerVibrateStateChanged();
        [PreserveSig] int __incomplete__remove_RingerVibrateStateChange();
        [PreserveSig] int __incomplete__SetVolumeGroupGainForId();
        [PreserveSig] int __incomplete__GetVolumeGroupGainForId();
        [PreserveSig] int __incomplete__GetActiveVolumeGroupForEndpointId();
        [PreserveSig] int __incomplete__GetVolumeGroupsForEndpoint();
        [PreserveSig] int __incomplete__GetCurrentVolumeContext();
        [PreserveSig] int __incomplete__SetVolumeGroupMuteForId();
        [PreserveSig] int __incomplete__GetVolumeGroupMuteForId();
        [PreserveSig] int __incomplete__SetRingerVibrateState();
        [PreserveSig] int __incomplete__GetRingerVibrateState();
        [PreserveSig] int __incomplete__SetPreferredChatApplication();
        [PreserveSig] int __incomplete__ResetPreferredChatApplication();
        [PreserveSig] int __incomplete__GetPreferredChatApplication();
        [PreserveSig] int __incomplete__GetCurrentChatApplications();
        [PreserveSig] int __incomplete__add_ChatContextChanged();
        [PreserveSig] int __incomplete__remove_ChatContextChanged();

        [PreserveSig]
        uint SetPersistedDefaultAudioEndpoint(int processId, DataFlow flow, Role role, IntPtr deviceId);

        [PreserveSig]
        uint GetPersistedDefaultAudioEndpoint(
            int processId,
            DataFlow flow,
            Role role,
            [Out, MarshalAs(UnmanagedType.HString)] out string deviceId);

        [PreserveSig]
        uint ClearAllPersistedApplicationDefaultEndpoints();
    }

    private sealed class HString : SafeHandle
    {
        private HString()
            : base(IntPtr.Zero, true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        public static HString Create(string value)
        {
            var result = WindowsCreateString(value, (uint)value.Length, out var hstring);
            if (result != 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            return new HString { handle = hstring };
        }

        protected override bool ReleaseHandle()
        {
            return WindowsDeleteString(handle) == 0;
        }

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            uint length,
            out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);
    }
}

