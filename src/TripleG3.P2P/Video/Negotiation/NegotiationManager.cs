using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TripleG3.P2P.Video.Negotiation;

public interface IVideoSessionController
{
    Task<SessionOffer> CreateOfferAsync(VideoSessionConfig cfg);
    Task<SessionAnswer> AcceptOfferAsync(SessionOffer offer);
    Task ApplyAnswerAsync(SessionAnswer answer);
    void RequestKeyFrame();
    Task RequestKeyFrameAsync(CancellationToken cancellationToken = default);
    event Action<SessionOffer>? OfferReceived;
    event Action<SessionAnswer>? AnswerReceived;
    event Action? PliReceived;
}

public sealed class NegotiationManager : IVideoSessionController
{
    private readonly IControlChannel _control;
    private readonly ILogger<NegotiationManager> _logger;
    private SessionOffer? _localOffer;
    private SessionAnswer? _remoteAnswer;

    public event Action<SessionOffer>? OfferReceived;
    public event Action<SessionAnswer>? AnswerReceived;
    public event Action? PliReceived;

    public NegotiationManager(IControlChannel control, ILogger<NegotiationManager>? logger = null)
    {
        _control = control;
        _logger = logger ?? NullLogger<NegotiationManager>.Instance;
        _control.MessageReceived += OnControlMessage;
    }

    private IVideoEncoder? _attachedEncoder;
    public void AttachEncoder(IVideoEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        _attachedEncoder = encoder;
    }

    public async Task<SessionOffer> CreateOfferAsync(VideoSessionConfig cfg)
    {
        var offer = new SessionOffer(
            cfg.Codec,
            cfg.ProfileLevelId,
            cfg.Width,
            cfg.Height,
            cfg.Bitrate,
            cfg.SpropParameterSets);
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
        ApplyAnswer(answer);
        return Task.CompletedTask;
    }

    public void RequestKeyFrame() => _ = SendKeyFrameRequestObservedAsync();

    public Task RequestKeyFrameAsync(CancellationToken cancellationToken = default)
        => SendAsync(NegotiationTypes.Pli, new { }, cancellationToken);

    private Task SendAsync(string type, object payload, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new { type, payload });
        return _control.SendReliableAsync(json, cancellationToken);
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
                ApplyAnswer(answer);
            }
            else if (type == NegotiationTypes.Pli)
            {
                _attachedEncoder?.RequestKeyFrame();
                PliReceived?.Invoke();
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Rejected malformed video negotiation message.");
        }
    }

    private void ApplyAnswer(SessionAnswer answer)
    {
        _remoteAnswer = answer;
        AnswerReceived?.Invoke(answer);
    }

    private async Task SendKeyFrameRequestObservedAsync()
    {
        try
        {
            await RequestKeyFrameAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Video keyframe request failed.");
        }
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
