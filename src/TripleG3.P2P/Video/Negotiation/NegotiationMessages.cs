namespace TripleG3.P2P.Video.Negotiation;

public static class NegotiationTypes
{
    public const string Offer = "offer";
    public const string Answer = "answer";
    public const string Pli = "pli";
}

public sealed record NegotiationEnvelope(string Type, object Payload);
