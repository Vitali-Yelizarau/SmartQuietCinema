using System.Runtime.InteropServices;
using System.Text;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>Resolves an active MMDevice render endpoint to its experimental PnP adapter before any audio client is opened.</summary>
internal static class EndpointIdentity
{
    private const string EndpointInstancePrefix = @"SWD\MMDEVAPI\";
    private const string EndpointHardwareId = @"MMDEVAPI\AudioEndpoints";
    private const string AdapterHardwareId = @"ROOT\SmartQuietCinemaVirtualAudio";
    private const uint ConfigRetSuccess = 0;
    private const uint ConfigRetNoSuchDevNode = 0x0D;
    private const uint ConfigRetBufferSmall = 0x1A;
    private const uint LocatePresentDevice = 0;
    private const uint ReservedFlags = 0;
    private const uint StringList = 0x2012;
    private const int MaximumInstanceIdLength = 200; // MAX_DEVICE_ID_LEN includes the terminator.
    private const int MaximumAncestorDepth = 16;
    private const int MaximumHardwareIdBytes = 64 * 1024; // Bound the allocation even if a property reports an unexpected size.
    private const int HardwareIdsPropertyId = 3;
    private static readonly DevicePropertyKey HardwareIdsKey = new() { FormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = HardwareIdsPropertyId };

    /// <summary>Resolves the present devnode for a complete PnP instance ID.</summary>
    internal delegate uint LocateDevNode(out uint node, string instanceId, uint flags);

    /// <summary>Returns the immediate parent handle for a present devnode.</summary>
    internal delegate uint ReadParent(out uint parent, uint node, uint flags);

    /// <summary>Reads a property or reports the number of bytes to allocate when queried with a null buffer.</summary>
    internal delegate uint ReadProperty(uint node, in DevicePropertyKey key, out uint type, byte[]? buffer, ref uint size, uint flags);

    /// <summary>Checks PnP ancestry for an endpoint obtained from the active render enumeration.</summary>
    /// <param name="endpointId">The opaque endpoint ID returned by MMDevice.</param>
    /// <param name="diagnostic">The failed operation and native status, or the reason the identity did not match.</param>
    /// <returns>Whether a present MMDevice software endpoint has the exact experimental adapter hardware ID in its ancestry.</returns>
    public static bool IsExpected(string endpointId, out string diagnostic) => IsExpected(endpointId, LocateDevice, GetParent, GetDeviceProperty, out diagnostic);

    /// <summary>Checks the same identity rules against native operations or deterministic Configuration Manager test responses.</summary>
    internal static bool IsExpected(string endpointId, LocateDevNode locate, ReadParent getParent, ReadProperty getProperty, out string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length >= MaximumInstanceIdLength - EndpointInstancePrefix.Length ||
            endpointId.Contains('\\') || endpointId.Contains('/') || endpointId.Any(char.IsControl))
        {
            diagnostic = "Malformed MMDevice endpoint ID.";
            return false;
        }

        // Windows 11 exposes this software devnode with the complete MMDevice ID as its instance component.
        string instanceId = EndpointInstancePrefix + endpointId;
        uint status = locate(out uint node, instanceId, LocatePresentDevice);
        if (status != ConfigRetSuccess)
        {
            diagnostic = $"CM_Locate_DevNodeW('{instanceId}') returned CONFIGRET 0x{status:X8}.";
            return false;
        }

        if (!TryReadHardwareIds(node, getProperty, out string[] endpointIds, out diagnostic)) return false;
        if (!endpointIds.Contains(EndpointHardwareId, StringComparer.OrdinalIgnoreCase))
        {
            diagnostic = $"The devnode '{instanceId}' has no {EndpointHardwareId} hardware ID.";
            return false;
        }

        for (int depth = 0; depth < MaximumAncestorDepth; depth++)
        {
            status = getParent(out uint parent, node, ReservedFlags);
            if (status != ConfigRetSuccess)
            {
                string context = status == ConfigRetNoSuchDevNode ? $"No ancestor has hardware ID {AdapterHardwareId}. " : "";
                diagnostic = $"{context}CM_Get_Parent(DEVINST 0x{node:X8}) returned CONFIGRET 0x{status:X8}.";
                return false;
            }

            if (!TryReadHardwareIds(parent, getProperty, out string[] hardwareIds, out diagnostic)) return false;
            if (hardwareIds.Contains(AdapterHardwareId, StringComparer.OrdinalIgnoreCase)) return true;
            node = parent;
        }

        diagnostic = $"No matching adapter within {MaximumAncestorDepth} ancestors of '{instanceId}'.";
        return false;
    }

    /// <summary>Reads and validates the complete byte-counted, double-NUL-terminated UTF-16 hardware ID property.</summary>
    private static bool TryReadHardwareIds(uint node, ReadProperty getProperty, out string[] hardwareIds, out string diagnostic)
    {
        hardwareIds = [];
        diagnostic = "";
        uint size = 0;
        uint status = getProperty(node, in HardwareIdsKey, out _, null, ref size, ReservedFlags);
        string operation = $"CM_Get_DevNode_PropertyW(DEVPKEY_Device_HardwareIds, DEVINST 0x{node:X8})";
        if (status != ConfigRetBufferSmall || size > MaximumHardwareIdBytes || size < 2 * sizeof(char) || size % sizeof(char) != 0)
        {
            diagnostic = $"{operation} sizing returned CONFIGRET 0x{status:X8}, {size} bytes.";
            return false;
        }

        var buffer = new byte[size];
        status = getProperty(node, in HardwareIdsKey, out uint type, buffer, ref size, ReservedFlags);
        if (status != ConfigRetSuccess || type != StringList || size > buffer.Length || size < 2 * sizeof(char) || size % sizeof(char) != 0)
        {
            diagnostic = $"{operation} read returned CONFIGRET 0x{status:X8}, type 0x{type:X}, {size} bytes.";
            return false;
        }

        string value = Encoding.Unicode.GetString(buffer, 0, (int)size);
        string entries = value[..^2];
        if (!value.EndsWith("\0\0", StringComparison.Ordinal) || entries.StartsWith('\0') || entries.EndsWith('\0') || entries.Contains("\0\0", StringComparison.Ordinal))
        {
            diagnostic = $"{operation} returned a malformed DEVPROP_TYPE_STRING_LIST.";
            return false;
        }

        if (entries.Length > 0) hardwareIds = entries.Split('\0');
        return true;
    }

    /// <summary>Matches the native DEVPROPKEY layout of a GUID and a 32-bit property ID.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DevicePropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>Resolves a PnP instance ID without creating or changing a devnode.</summary>
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint LocateDevice(out uint node, string instanceId, uint flags);

    /// <summary>Reads a devnode's parent in the present PnP tree.</summary>
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent", ExactSpelling = true)]
    private static extern uint GetParent(out uint parent, uint node, uint flags);

    /// <summary>Queries the required byte count or reads the PnP property into a caller-owned buffer.</summary>
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", ExactSpelling = true)]
    private static extern uint GetDeviceProperty(uint node, in DevicePropertyKey key, out uint type, [Out] byte[]? buffer, ref uint size, uint flags);
}
