using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>
/// Verifies the PnP ancestry of an explicit endpoint before the probe can open any test stream.
/// </summary>
internal static class EndpointIdentity
{
    private const string HardwareId = @"ROOT\SmartQuietCinemaVirtualAudio";
    private const uint StringList = 0x2012;
    private const int HardwareIdsPropertyId = 3;
    private static readonly DevicePropertyKey HardwareIdsKey = new() { FormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = HardwareIdsPropertyId };

    /// <summary>
    /// Matches the adapter's hardware ID rather than a user-editable friendly name. Missing identity fails closed.
    /// </summary>
    /// <param name="endpoint">The endpoint resolved by the caller's explicit ID.</param>
    /// <returns>Whether the active render endpoint belongs to the experimental adapter.</returns>
    public static bool IsExpected(MMDevice endpoint)
    {
        const int ConfigRetSuccess = 0;
        const int LocatePresentDevice = 0;
        const int ReservedFlags = 0;
        const int HardwareIdBufferBytes = 4096;
        const int MaximumAncestorDepth = 16;

        if (endpoint.DataFlow != DataFlow.Render || endpoint.State != DeviceState.Active ||
            !endpoint.Properties.TryGetValue(PropertyKeys.PKEY_Device_InstanceId, out string? instanceId) ||
            string.IsNullOrEmpty(instanceId) || LocateDevice(out uint node, instanceId, LocatePresentDevice) != ConfigRetSuccess)
            return false;

        var buffer = new byte[HardwareIdBufferBytes];
        for (int depth = 0; depth < MaximumAncestorDepth; depth++)
        {
            uint size = (uint)buffer.Length;
            if (GetDeviceProperty(node, in HardwareIdsKey, out uint type, buffer, ref size, ReservedFlags) == ConfigRetSuccess && type == StringList && size <= buffer.Length)
            {
                string[] ids = Encoding.Unicode.GetString(buffer, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (ids.Contains(HardwareId, StringComparer.OrdinalIgnoreCase)) return true;
            }
            if (GetParent(out uint parent, node, ReservedFlags) != ConfigRetSuccess) break;

            node = parent;
        }
        return false;
    }

    /// <summary>Matches the native CONFIGRET property API's DEVPROPKEY layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DevicePropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>Resolves a PnP instance ID without creating or changing a devnode.</summary>
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint LocateDevice(out uint node, string instanceId, uint flags);

    /// <summary>Reads the parent devnode so an MMDevice software endpoint can be traced to its adapter.</summary>
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent", ExactSpelling = true)]
    private static extern uint GetParent(out uint parent, uint node, uint flags);

    /// <summary>Reads device hardware IDs as an unmodified PnP string list.</summary>
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", ExactSpelling = true)]
    private static extern uint GetDeviceProperty(uint node, in DevicePropertyKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
}
