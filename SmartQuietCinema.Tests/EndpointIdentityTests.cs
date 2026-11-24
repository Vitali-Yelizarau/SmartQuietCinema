using System.Text;
using SmartQuietCinema.Tools.VirtualAudioProbe;

namespace SmartQuietCinema.Tests;

/// <summary>Exercises the probe's present-device lookup and native response handling without opening an audio endpoint.</summary>
public sealed class EndpointIdentityTests
{
    private const string EndpointId = "{0.0.0.00000000}.{230d41d0-1363-46a1-82fd-52807ece0923}";
    private const uint EndpointNode = 1;
    private const uint AdapterNode = 2;
    private const uint OtherAncestor = 3;
    private const uint Success = 0;
    private const uint BufferSmall = 0x1A;
    private const uint NoSuchDevNode = 0x0D;
    private const uint NoSuchValue = 0x25;
    private const uint StringListType = 0x2012;

    /// <summary>Reproduces the VMware software endpoint and its DevGen parent, requiring a complete case-insensitive match in the parent's hardware ID list.</summary>
    [Fact]
    public void ResolvesVmwareSoftwareEndpointToExperimentalParent()
    {
        var native = new DeviceTree();
        Assert.True(native.Check(EndpointId, out string diagnostic), diagnostic);
        Assert.Equal(@"SWD\MMDEVAPI\" + EndpointId, native.LocatedInstanceId);
        Assert.Equal(new[] { EndpointNode }, native.ParentQueries);
        Assert.Equal(new[] { EndpointNode, EndpointNode, AdapterNode, AdapterNode }, native.PropertyQueries);
    }

    /// <summary>Rejects an unrelated endpoint or a near-match adapter ID, even when another present node has the real hardware ID.</summary>
    [Theory]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_1234")]
    [InlineData(@"ROOT\SmartQuietCinemaVirtualAudio_Extra")]
    [InlineData(@"SOME\ROOT\SmartQuietCinemaVirtualAudio")]
    public void RejectsUnrelatedOrIncorrectHardwareIds(string hardwareId)
    {
        var native = new DeviceTree { ParentIds = hardwareId + "\0\0" };
        Assert.False(native.Check(EndpointId, out string diagnostic));
        Assert.Contains("No ancestor has hardware ID", diagnostic);
        Assert.DoesNotContain(OtherAncestor, native.PropertyQueries);
    }

    /// <summary>Ensures a different kind of devnode cannot pass solely because its parent happens to expose the experimental hardware ID.</summary>
    [Fact]
    public void RequiresMmDeviceSoftwareEndpointHardwareIdentity()
    {
        var native = new DeviceTree { ChildIds = "OTHER\\Endpoint\0\0" };
        Assert.False(native.Check(EndpointId, out string diagnostic));
        Assert.Contains(@"MMDEVAPI\AudioEndpoints", diagnostic);
        Assert.Empty(native.ParentQueries);
    }

    /// <summary>Rejects malformed or truncated native input before passing a composed identifier to Windows.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{test}\0ignored")]
    [InlineData(@"SWD\MMDEVAPI\AlreadyPrefixed")]
    [InlineData("x/y")]
    public void RejectsMalformedIdsBeforeNativeLookup(string endpointId)
    {
        var native = new DeviceTree();
        Assert.False(native.Check(endpointId, out string diagnostic));
        Assert.Contains("Malformed", diagnostic);
        Assert.Null(native.LocatedInstanceId);
    }

    /// <summary>Checks that the WDK length limit includes the prefix and NUL terminator, preventing native lookup with an overlong instance ID.</summary>
    [Fact]
    public void RejectsEndpointWhoseDeviceInstanceIdWouldExceedNativeLimit()
    {
        var native = new DeviceTree();
        Assert.False(native.Check(new string('x', 200 - @"SWD\MMDEVAPI\".Length), out _));
        Assert.Null(native.LocatedInstanceId);
    }

    /// <summary>Rejects a failed present-device lookup and reports its CONFIGRET without following an undefined handle.</summary>
    [Fact]
    public void ReportsNativeLookupFailure()
    {
        var native = new DeviceTree { LocateStatus = NoSuchDevNode };
        Assert.False(native.Check(EndpointId, out string diagnostic));
        Assert.Contains("CM_Locate_DevNodeW", diagnostic);
        Assert.Contains("0x0000000D", diagnostic);
        Assert.Empty(native.ParentQueries);
        Assert.Empty(native.PropertyQueries);
    }

    /// <summary>Rejects failed property sizing or reading and a failed parent traversal, preserving the responsible API and CONFIGRET.</summary>
    [Theory]
    [InlineData("sizing", "CM_Get_DevNode_PropertyW", "0x00000025")]
    [InlineData("read", "CM_Get_DevNode_PropertyW", "0x00000025")]
    [InlineData("parent", "CM_Get_Parent", "0x00000025")]
    public void ReportsFailedNativeOperations(string stage, string api, string status)
    {
        var native = new DeviceTree { FailedStage = stage };
        Assert.False(native.Check(EndpointId, out string diagnostic));
        Assert.Contains(api, diagnostic);
        Assert.Contains(status, diagnostic);
    }

    /// <summary>Rejects the exact expected text when returned with the wrong DEVPROPTYPE or in a malformed native string list.</summary>
    [Theory]
    [InlineData(0x12u, "ROOT\\SmartQuietCinemaVirtualAudio\0\0", "type 0x12")]
    [InlineData(StringListType, "ROOT\\SmartQuietCinemaVirtualAudio\0", "malformed")]
    [InlineData(StringListType, "ROOT\\SmartQuietCinemaVirtualAudio\0\0tail\0\0", "malformed")]
    [InlineData(StringListType, "ROOT\\SmartQuietCinemaVirtualAudio\0\0\0", "malformed")]
    public void RejectsWrongPropertyTypeOrMalformedMultiString(uint type, string raw, string reason)
    {
        var native = new DeviceTree { ParentIds = raw, ParentPropertyType = type };
        Assert.False(native.Check(EndpointId, out string diagnostic));
        Assert.Contains(reason, diagnostic);
    }

    /// <summary>Accepts a valid list larger than the former fixed buffer because native sizing now defines the byte count.</summary>
    [Fact]
    public void ReadsCompleteNativeStringListBeyondOriginalBuffer()
    {
        var native = new DeviceTree { ParentIds = string.Concat(Enumerable.Repeat("ACME\\OtherId\0", 200)) + "ROOT\\SmartQuietCinemaVirtualAudio\0\0" };
        Assert.True(native.Check(EndpointId, out string diagnostic), diagnostic);
    }

    /// <summary>Rejects invalid reported sizes before allocation or decoding to prevent trusting stale or out-of-range data.</summary>
    [Theory]
    [InlineData(65538u, false)]
    [InlineData(5u, false)]
    [InlineData(65538u, true)]
    [InlineData(5u, true)]
    public void RejectsInvalidNativeSize(uint byteCount, bool duringRead)
    {
        var native = new DeviceTree { ReportedSize = byteCount, ReportSizeDuringRead = duringRead };
        Assert.False(native.Check(EndpointId, out string diagnostic));
        Assert.Contains($"{byteCount} bytes", diagnostic);
    }

    /// <summary>Models CONFIGRET and byte-counted DEVPROPKEY responses for a software endpoint and its present PnP parent.</summary>
    private sealed class DeviceTree
    {
        public uint LocateStatus { get; init; }
        public string? FailedStage { get; init; }
        public string ChildIds { get; init; } = "mmdevapi\\audioendpoints\0\0";
        public string ParentIds { get; init; } = "ROOT\\OtherCompatibleId\0root\\smartquietcinemavirtualaudio\0\0";
        public uint ParentPropertyType { get; init; } = StringListType;
        public uint? ReportedSize { get; init; }
        public bool ReportSizeDuringRead { get; init; }
        public string? LocatedInstanceId { get; private set; }
        public List<uint> ParentQueries { get; } = [];
        public List<uint> PropertyQueries { get; } = [];

        /// <summary>Runs the production resolver against the three native test operations.</summary>
        public bool Check(string endpointId, out string diagnostic) => EndpointIdentity.IsExpected(endpointId, Locate, Parent, ReadIds, out diagnostic);

        /// <summary>Returns the fixture devnode only for the complete software instance ID reported by the observed endpoint.</summary>
        private uint Locate(out uint node, string instanceId, uint flags)
        {
            LocatedInstanceId = instanceId;
            node = EndpointNode;
            Assert.Equal(0u, flags);
            return string.Equals(instanceId, @"SWD\MMDEVAPI\" + EndpointId, StringComparison.OrdinalIgnoreCase) ? LocateStatus : NoSuchDevNode;
        }

        /// <summary>Models the actual software endpoint-to-adapter edge and optionally fails the parent operation.</summary>
        private uint Parent(out uint parent, uint node, uint flags)
        {
            Assert.Equal(0u, flags);
            ParentQueries.Add(node);
            parent = AdapterNode;
            return FailedStage == "parent" ? NoSuchValue : node == EndpointNode ? Success : NoSuchDevNode;
        }

        /// <summary>Checks the exact hardware-ID DEVPROPKEY and returns controlled native sizing and property-read responses.</summary>
        private uint ReadIds(uint node, in EndpointIdentity.DevicePropertyKey key, out uint type, byte[]? buffer, ref uint size, uint flags)
        {
            PropertyQueries.Add(node);
            Assert.Equal(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), key.FormatId);
            Assert.Equal(3u, key.PropertyId);
            Assert.Equal(0u, flags);
            type = node == AdapterNode ? ParentPropertyType : StringListType;
            var bytes = Encoding.Unicode.GetBytes(node == EndpointNode ? ChildIds : node == AdapterNode ? ParentIds : "ROOT\\SmartQuietCinemaVirtualAudio\0\0");
            if (buffer is null)
            {
                Assert.Equal(0u, size);
                size = node == AdapterNode && !ReportSizeDuringRead && ReportedSize.HasValue ? ReportedSize.Value : (uint)bytes.Length;
                return node == AdapterNode && FailedStage == "sizing" ? NoSuchValue : BufferSmall;
            }

            Assert.Equal((uint)bytes.Length, size);
            bytes.CopyTo(buffer, 0);
            if (node == AdapterNode && ReportSizeDuringRead && ReportedSize.HasValue) size = ReportedSize.Value;
            return node == AdapterNode && FailedStage == "read" ? NoSuchValue : Success;
        }
    }
}
