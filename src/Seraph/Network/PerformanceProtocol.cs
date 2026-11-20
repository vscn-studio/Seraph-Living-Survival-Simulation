using ProtoBuf;

namespace Seraph.Network;

// Separate optional channel, leaving gameplay protocol compatibility unchanged.
[ProtoContract]
public sealed class PerformancePing
{
    [ProtoMember(1)] public int Version { get; set; } = 1;
    [ProtoMember(2)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(3)] public long Id { get; set; }
    [ProtoMember(4)] public bool RecordServer { get; set; }
    [ProtoMember(5)] public string CaptureId { get; set; } = "";
}
[ProtoContract]
public sealed class PerformancePong
{
    [ProtoMember(1)] public string SessionEpoch { get; set; } = "";
    [ProtoMember(2)] public long Id { get; set; }
    [ProtoMember(3)] public double ServerQueueMs { get; set; }
    [ProtoMember(4)] public bool ServerRecording { get; set; }
    [ProtoMember(5)] public string CaptureId { get; set; } = "";
}
