using System.Buffers;
using TripleG3.P2P.Video;
using TripleG3.P2P.Video.Internal;
using TripleG3.P2P.Video.Primitives;
using TripleG3.P2P.Video.Rtp;
using Xunit;
using StableReceiver = TripleG3.P2P.Video.RtpVideoReceiver;
using StableSender = TripleG3.P2P.Video.RtpVideoSender;
using VideoAccessUnit = TripleG3.P2P.Video.EncodedAccessUnit;

namespace TripleG3.P2P.UnitTests;

public sealed class VideoHardeningTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(3000)]
    public void Configuration_Depacketizer_RoundTrips_Exact_AnnexB_Bytes(int nalLength)
    {
        var nal = CreateNal(nalLength, 0x65);
        var annexB = BuildAnnexB(nal);
        using var source = new VideoAccessUnit(annexB, true, 90000, 0);
        var packetizer = new Packetizer(120, new SequenceNumberGenerator());
        var depacketizer = new Depacketizer();
        VideoAccessUnit? completed = null;

        foreach (var segment in packetizer.Packetize(source, 96, 0x1234))
        {
            if (depacketizer.AddPacket(segment.AsSpan(), out var accessUnit)) completed = accessUnit;
            ArrayPool<byte>.Shared.Return(segment.Array!);
        }

        Assert.NotNull(completed);
        Assert.Equal(annexB, completed.Value.AnnexB.ToArray());
        Assert.True(completed.Value.IsKeyFrame);
        completed.Value.Dispose();
    }

    [Fact]
    public void Cipher_With_Overhead_RoundTrips_Fragmented_Frame_Within_Mtu()
    {
        const int mtu = 180;
        var cipher = new TaggedVideoCipher();
        var packets = new List<ReadOnlyMemory<byte>>();
        using var sender = new StableSender(0x4321, mtu, cipher, packet => packets.Add(packet));
        using var receiver = new StableReceiver(cipher);
        VideoAccessUnit? completed = null;
        receiver.AccessUnitReceived += accessUnit => completed = accessUnit;
        var annexB = BuildAnnexB(CreateNal(3000, 0x65));
        using var source = new VideoAccessUnit(annexB, true, 12345, 0);

        sender.Send(source);
        Assert.All(packets, packet => Assert.InRange(packet.Length, 1, mtu));
        foreach (var packet in packets) receiver.ProcessRtp(packet.Span);

        Assert.NotNull(completed);
        Assert.Equal(annexB, completed.Value.AnnexB.ToArray());
        completed.Value.Dispose();
    }

    [Fact]
    public void Cipher_Rejects_Tampered_Payload()
    {
        var cipher = new TaggedVideoCipher();
        var packets = new List<ReadOnlyMemory<byte>>();
        using var sender = new StableSender(0x4321, 1200, cipher, packet => packets.Add(packet));
        using var receiver = new StableReceiver(cipher);
        var delivered = 0;
        receiver.AccessUnitReceived += accessUnit =>
        {
            Interlocked.Increment(ref delivered);
            accessUnit.Dispose();
        };
        using var source = new VideoAccessUnit(BuildAnnexB(CreateNal(20, 0x65)), true, 12345, 0);
        sender.Send(source);
        var tampered = packets.Single().ToArray();
        tampered[^1] ^= 0xFF;

        receiver.ProcessRtp(tampered);

        Assert.Equal(0, Volatile.Read(ref delivered));
    }

    [Fact]
    public void Packet_Loss_Drops_Damaged_Frame_And_Delivers_Following_Frame()
    {
        var cipher = new TripleG3.P2P.Video.NoOpCipher();
        var packets = new List<ReadOnlyMemory<byte>>();
        using var sender = new StableSender(0x88, 180, cipher, packet => packets.Add(packet));
        using var receiver = new StableReceiver(cipher);
        var received = new List<byte[]>();
        receiver.AccessUnitReceived += accessUnit =>
        {
            received.Add(accessUnit.AnnexB.ToArray());
            accessUnit.Dispose();
        };

        var damagedFrame = BuildAnnexB(CreateNal(1000, 0x65));
        var followingFrame = BuildAnnexB(CreateNal(20, 0x61));
        using var first = new VideoAccessUnit(damagedFrame, true, 1000, 0);
        using var second = new VideoAccessUnit(followingFrame, false, 2000, 0);
        sender.Send(first);
        var firstPackets = packets.ToArray();
        packets.Clear();
        sender.Send(second);
        var secondPackets = packets.ToArray();

        for (var index = 0; index < firstPackets.Length; index++)
        {
            if (index != 1) receiver.ProcessRtp(firstPackets[index].Span);
        }

        foreach (var packet in secondPackets) receiver.ProcessRtp(packet.Span);

        Assert.Single(received);
        Assert.Equal(followingFrame, received[0]);
        Assert.True(receiver.GetStats().PacketsLost >= 1);
    }

    [Fact]
    public void Receiver_Rejects_Unexpected_Ssrc_And_PayloadType()
    {
        var config = new RtpVideoReceiverConfig
        {
            ExpectedSsrc = 0x22,
            PayloadType = 96
        };
        using var receiver = new StableReceiver(config);
        var delivered = 0;
        receiver.AccessUnitReceived += accessUnit =>
        {
            Interlocked.Increment(ref delivered);
            accessUnit.Dispose();
        };
        using var source = new VideoAccessUnit(BuildAnnexB(CreateNal(20, 0x65)), true, 1000, 0);
        var wrongSource = new H264RtpPacketizer(0x11, 1200, 96, new TripleG3.P2P.Video.Security.NoOpCipher());
        var wrongPayload = new H264RtpPacketizer(0x22, 1200, 97, new TripleG3.P2P.Video.Security.NoOpCipher());
        var valid = new H264RtpPacketizer(0x22, 1200, 96, new TripleG3.P2P.Video.Security.NoOpCipher());

        receiver.ProcessRtp(wrongSource.Packetize(source).Single().Span);
        receiver.ProcessRtp(wrongPayload.Packetize(source).Single().Span);
        receiver.ProcessRtp(valid.Packetize(source).Single().Span);

        Assert.Equal(1, Volatile.Read(ref delivered));
        Assert.Equal<uint>(1, receiver.GetStats().PacketsReceived);
    }

    [Fact]
    public void Depacketizer_Drops_Oversized_Frame_Then_Recovers()
    {
        var depacketizer = new H264RtpDepacketizer(
            new TripleG3.P2P.Video.Security.NoOpCipher(),
            maximumFrames: 2,
            maximumFrameBytes: 128,
            maximumNalBytes: 100,
            maximumAssemblyAge: TimeSpan.FromMilliseconds(100));
        var packetizer = new H264RtpPacketizer(0x33, 80, new TripleG3.P2P.Video.Security.NoOpCipher());
        using var oversized = new VideoAccessUnit(BuildAnnexB(CreateNal(200, 0x65)), true, 1000, 0);
        using var valid = new VideoAccessUnit(BuildAnnexB(CreateNal(20, 0x65)), true, 2000, 0);

        Assert.DoesNotContain(packetizer.Packetize(oversized), packet => depacketizer.TryProcessPacket(packet.Span, out _));
        VideoAccessUnit completed = default;
        var delivered = false;
        foreach (var packet in packetizer.Packetize(valid))
        {
            if (!depacketizer.TryProcessPacket(packet.Span, out completed)) continue;
            delivered = true;
            break;
        }

        Assert.True(delivered);
        Assert.Equal(valid.AnnexB.ToArray(), completed.AnnexB.ToArray());
        completed.Dispose();
    }

    [Fact]
    public void Sequence_Number_Generator_Is_Concurrent_And_Wraps()
    {
        var wrapping = new RtpSequenceNumberGenerator(65534);
        Assert.Equal(ushort.MaxValue, wrapping.Next());
        Assert.Equal((ushort)0, wrapping.Next());

        var concurrent = new RtpSequenceNumberGenerator();
        var values = new ushort[1000];
        Parallel.For(0, values.Length, index => values[index] = concurrent.Next());
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    private static byte[] CreateNal(int length, byte header)
    {
        var nal = new byte[length];
        nal[0] = header;
        for (var index = 1; index < nal.Length; index++) nal[index] = (byte)(index % 251);
        return nal;
    }

    private static byte[] BuildAnnexB(byte[] nal)
    {
        var annexB = new byte[nal.Length + 4];
        annexB[3] = 1;
        Buffer.BlockCopy(nal, 0, annexB, 4, nal.Length);
        return annexB;
    }

}