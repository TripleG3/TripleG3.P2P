using System.Threading.Tasks;
using TripleG3.P2P.Video;
using TripleG3.P2P.Video.Negotiation;
using Xunit;

namespace TripleG3.P2P.VideoTests;

public class PliKeyFrameTests
{
    private sealed class FakeEncoder : IVideoEncoder
    {
        public event Action<EncodedAccessUnit>? AccessUnitReady { add {} remove {} }
        public bool KeyRequested;
        public void RequestKeyFrame() { KeyRequested = true; }
    }

    [Fact]
    public async Task Pli_Triggers_Keyframe_Request_On_Remote()
    {
    var chA = new TripleG3.P2P.Video.InMemoryControlChannel();
    var chB = new TripleG3.P2P.Video.InMemoryControlChannel();
        chA.MessageReceived += m => chB.SendReliableAsync(m);
        chB.MessageReceived += m => chA.SendReliableAsync(m);
        var mgrA = new NegotiationManager(chA);
        var mgrB = new NegotiationManager(chB);
        var encB = new FakeEncoder();
        mgrB.AttachEncoder(encB);
        // Simulate offer/answer so channel established
        var cfg = new VideoSessionConfig(640,360,500_000,30);
        await mgrA.CreateOfferAsync(cfg);
        await Task.Delay(50);
        // A requests keyframe (sends PLI) -> B should invoke encoder.RequestKeyFrame
        mgrA.RequestKeyFrame();
        await Task.Delay(50);
        Assert.True(encB.KeyRequested);
    }
}
