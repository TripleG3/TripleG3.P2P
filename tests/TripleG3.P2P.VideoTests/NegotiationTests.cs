using TripleG3.P2P.Video;
using TripleG3.P2P.Video.Negotiation;
using Xunit;

namespace TripleG3.P2P.VideoTests;

public class NegotiationTests
{
    [Fact]
    public async Task Negotiation_RoundTrip_OfferAnswer()
    {
        var chA = new InMemoryControlChannel();
        var chB = new InMemoryControlChannel();
        // Wire channels (simple bridge)
        chA.MessageReceived += m => chB.SendReliableAsync(m);
        chB.MessageReceived += m => chA.SendReliableAsync(m);

        var mgrA = new NegotiationManager(chA);
        var mgrB = new NegotiationManager(chB);

        SessionOffer? offerOnB = null;
        SessionAnswer? answerOnA = null;
        mgrB.OfferReceived += o => { offerOnB = o; _ = mgrB.AcceptOfferAsync(o); };
        mgrA.AnswerReceived += a => answerOnA = a;

        var cfg = new VideoSessionConfig(1280,720,2_000_000,30);
        await mgrA.CreateOfferAsync(cfg);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((offerOnB == null || answerOnA == null) && sw.ElapsedMilliseconds < 1000)
            await Task.Delay(10);

        Assert.NotNull(offerOnB);
        Assert.NotNull(answerOnA);
        Assert.True(answerOnA!.Accepted);
    }
}
