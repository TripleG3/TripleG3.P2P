using System.Text.Json;

namespace TripleG3.P2P.Video.Negotiation;

public interface IVideoSessionController
{
    Task<SessionOffer> CreateOfferAsync(VideoSessionConfig cfg);
    Task<SessionAnswer> AcceptOfferAsync(SessionOffer offer);
    Task ApplyAnswerAsync(SessionAnswer answer);
    void RequestKeyFrame();
    event Action<SessionOffer>? OfferReceived;
    event Action<SessionAnswer>? AnswerReceived;
    event Action? PliReceived;
}

public sealed class NegotiationManager : IVideoSessionController
{
    private readonly IControlChannel _control;
    private SessionOffer? _localOffer;
    private SessionAnswer? _remoteAnswer;

    public event Action<SessionOffer>? OfferReceived;
    public event Action<SessionAnswer>? AnswerReceived;
    public event Action? PliReceived;

    public NegotiationManager(IControlChannel control)
    {
        _control = control;
        _control.MessageReceived += OnControlMessage;
    }

    private IVideoEncoder? _attachedEncoder;
    public void AttachEncoder(IVideoEncoder encoder)
    {
        _attachedEncoder = encoder;
        PliReceived += () => _attachedEncoder?.RequestKeyFrame();
    }

    public async Task<SessionOffer> CreateOfferAsync(VideoSessionConfig cfg)
    {
        var offer = new SessionOffer(cfg.Codec, "42e01f", cfg.Width, cfg.Height, cfg.Bitrate, ""); // TODO supply SPS/PPS
        _localOffer = offer;
        await SendAsync(NegotiationTypes.Offer, offer);
        return offer;
    }

    public async Task<SessionAnswer> AcceptOfferAsync(SessionOffer offer)
    {
        // For phase1 we always accept if codec matches
        var answer = new SessionAnswer(offer.Codec == "H264", offer.Codec, offer.ProfileLevelId, offer.SpropParameterSets);
        _remoteAnswer = answer;
        await SendAsync(NegotiationTypes.Answer, answer);
        return answer;
    }

    public Task ApplyAnswerAsync(SessionAnswer answer)
    {
        _remoteAnswer = answer;
        AnswerReceived?.Invoke(answer);
        return Task.CompletedTask;
    }

    public void RequestKeyFrame() => SendAsync(NegotiationTypes.Pli, new { });

    private Task SendAsync(string type, object payload)
    {
        var json = JsonSerializer.Serialize(new { type, payload });
        return _control.SendReliableAsync(json);
    }

    private void OnControlMessage(string msg)
    {
        try
        {
            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == NegotiationTypes.Offer)
            {
                var p = root.GetProperty("payload");
                string codec = GetProp(p, "codec");
                string profile = GetProp(p, "profileLevelId");
                int width = int.Parse(GetProp(p, "width"));
                int height = int.Parse(GetProp(p, "height"));
                int bitrate = int.Parse(GetProp(p, "bitrate"));
                string sprop = GetProp(p, "spropParameterSets");
                var offer = new SessionOffer(codec, profile, width, height, bitrate, sprop);
                OfferReceived?.Invoke(offer);
            }
            else if (type == NegotiationTypes.Answer)
            {
                var p = root.GetProperty("payload");
                bool accepted = bool.Parse(GetProp(p, "accepted"));
                string codec = GetProp(p, "codec");
                string profile = GetProp(p, "profileLevelId");
                string sprop = GetProp(p, "spropParameterSets");
                var answer = new SessionAnswer(accepted, codec, profile, sprop);
                ApplyAnswerAsync(answer).GetAwaiter().GetResult();
            }
            else if (type == NegotiationTypes.Pli)
            {
                PliReceived?.Invoke();
            }
        }
        catch { /* swallow malformed */ }
    }

    private static string GetProp(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out var val)) return val.ValueKind == JsonValueKind.String ? val.GetString()! : val.ToString();
        // Try PascalCase
        var alt = char.ToUpperInvariant(name[0]) + name.Substring(1);
        if (parent.TryGetProperty(alt, out val)) return val.ValueKind == JsonValueKind.String ? val.GetString()! : val.ToString();
        return string.Empty;
    }
}
