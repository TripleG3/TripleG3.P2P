namespace TripleG3.P2P.Audio;

/// <summary>RTP metadata for an Opus audio frame.</summary>
public sealed record AudioFrameMetadata(uint Timestamp, bool Marker = false);

/// <summary>An inbound Opus frame and its RTP metadata.</summary>
public sealed record ReceivedAudioFrame(ReadOnlyMemory<byte> OpusFrame, uint Timestamp, ushort SequenceNumber, bool IsGap);