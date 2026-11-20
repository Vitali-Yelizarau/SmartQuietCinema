#pragma once

// Copyright (c) Microsoft Corporation All Rights Reserved
// Adapted from Microsoft's MS-PL-licensed SysVAD speaker and Render3 endpoint tables.
// See LICENSE.SysVAD.txt and README.md for notices and the exact upstream revision.
#include "SysVadShared.h"
#include "speakertopo.h"
#include "speakertoptable.h"

NTSTATUS CreateMiniportWaveRTSYSVAD(PUNKNOWN*, REFCLSID, PUNKNOWN, POOL_FLAGS, PUNKNOWN, PVOID, PENDPOINT_MINIPAIR);
NTSTATUS CreateMiniportTopologySYSVAD(PUNKNOWN*, REFCLSID, PUNKNOWN, POOL_FLAGS, PUNKNOWN, PVOID, PENDPOINT_MINIPAIR);

/// <summary>Builds a fixed stereo PCM16 format so the advertised rates and buffer alignment agree.</summary>
static constexpr KSDATAFORMAT_WAVEFORMATEXTENSIBLE StereoFormat(ULONG rate)
{
    return {
        { sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE), 0, 0, 0, STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
          STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM), STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX) },
        { { WAVE_FORMAT_EXTENSIBLE, 2, rate, rate * 4, 4, 16, sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX) },
          16, KSAUDIO_SPEAKER_STEREO, STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM) }
    };
}

static KSDATAFORMAT_WAVEFORMATEXTENSIBLE VirtualFormats[] = { StereoFormat(48000), StereoFormat(44100) };
static MODE_AND_DEFAULT_FORMAT VirtualModes[] = {
    { STATIC_AUDIO_SIGNALPROCESSINGMODE_DEFAULT, &VirtualFormats[0].DataFormat },
    { STATIC_AUDIO_SIGNALPROCESSINGMODE_RAW, &VirtualFormats[0].DataFormat }
};
static PIN_DEVICE_FORMATS_AND_MODES VirtualPinFormats[] = {
    { SystemRenderPin, VirtualFormats, ARRAYSIZE(VirtualFormats), VirtualModes, ARRAYSIZE(VirtualModes) },
    { BridgePin, nullptr, 0, nullptr, 0 }
};

static KSDATARANGE_AUDIO VirtualRenderRange = {
    { sizeof(KSDATARANGE_AUDIO), KSDATARANGE_ATTRIBUTES, 0, 0, STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
      STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM), STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX) },
    2, 16, 16, 44100, 48000
};
static PKSDATARANGE VirtualRenderRanges[] = {
    reinterpret_cast<PKSDATARANGE>(&VirtualRenderRange), reinterpret_cast<PKSDATARANGE>(&PinDataRangeAttributeList)
};

// Render3 is the existing SysVAD two-pin layout with no offload or hardware loopback.
static PCPIN_DESCRIPTOR VirtualWavePins[] = {
    { 1, 1, 0, nullptr, { 0, nullptr, 0, nullptr, ARRAYSIZE(VirtualRenderRanges), VirtualRenderRanges,
        KSPIN_DATAFLOW_IN, KSPIN_COMMUNICATION_SINK, &KSCATEGORY_AUDIO, nullptr, 0 } },
    { 0, 0, 0, nullptr, { 0, nullptr, 0, nullptr, ARRAYSIZE(SpeakerTopoPinDataRangePointersBridge), SpeakerTopoPinDataRangePointersBridge,
        KSPIN_DATAFLOW_OUT, KSPIN_COMMUNICATION_NONE, &KSCATEGORY_AUDIO, nullptr, 0 } }
};
static PCNODE_DESCRIPTOR VirtualWaveNodes[] = { { 0, nullptr, &KSNODETYPE_DAC, nullptr } };
static PCCONNECTION_DESCRIPTOR VirtualWaveConnections[] = {
    { PCFILTER_NODE, KSPIN_WAVE_RENDER3_SINK_SYSTEM, KSNODE_WAVE_DAC, 1 },
    { KSNODE_WAVE_DAC, 0, PCFILTER_NODE, KSPIN_WAVE_RENDER3_SOURCE }
};
static PCPROPERTY_ITEM VirtualWaveProperties[] = {
    { &KSPROPSETID_Pin, KSPROPERTY_PIN_PROPOSEDATAFORMAT, KSPROPERTY_TYPE_SET | KSPROPERTY_TYPE_BASICSUPPORT, PropertyHandler_WaveFilter },
    { &KSPROPSETID_Pin, KSPROPERTY_PIN_PROPOSEDATAFORMAT2, KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT, PropertyHandler_WaveFilter }
};
DEFINE_PCAUTOMATION_TABLE_PROP(VirtualWaveAutomation, VirtualWaveProperties);
static PCFILTER_DESCRIPTOR VirtualWaveDescriptor = {
    0, &VirtualWaveAutomation, sizeof(PCPIN_DESCRIPTOR), ARRAYSIZE(VirtualWavePins), VirtualWavePins,
    sizeof(PCNODE_DESCRIPTOR), ARRAYSIZE(VirtualWaveNodes), VirtualWaveNodes,
    ARRAYSIZE(VirtualWaveConnections), VirtualWaveConnections, 0, nullptr
};
static PHYSICALCONNECTIONTABLE VirtualPhysicalConnections[] = {
    { KSPIN_TOPO_WAVEOUT_SOURCE, KSPIN_WAVE_RENDER3_SOURCE, CONNECTIONTYPE_WAVE_OUTPUT }
};
static ENDPOINT_MINIPAIR VirtualRender = {
    eSpeakerDevice, L"SqcVirtualTopology", nullptr, CreateMiniportTopologySYSVAD, &SpeakerTopoMiniportFilterDescriptor, 0, nullptr,
    L"SqcVirtualWave", nullptr, CreateMiniportWaveRTSYSVAD, &VirtualWaveDescriptor, 0, nullptr,
    2, VirtualPinFormats, ARRAYSIZE(VirtualPinFormats), VirtualPhysicalConnections, ARRAYSIZE(VirtualPhysicalConnections),
    ENDPOINT_NO_FLAGS, nullptr, 0, nullptr
};

static PENDPOINT_MINIPAIR g_RenderEndpoints[] = { &VirtualRender };
constexpr ULONG g_cRenderEndpoints = ARRAYSIZE(g_RenderEndpoints);
constexpr ULONG g_MaxMiniports = 2;
