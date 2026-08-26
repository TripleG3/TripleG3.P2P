using System.Globalization;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using TripleG3.P2P.Hubs;
using TripleG3.P2P.Serialization;
using Xunit;

namespace TripleG3.P2P.UnitTests;

public sealed class SerializerTests
{
    [Fact]
    public void LengthPrefixed_RoundTrips_Delimiters_Nulls_Nesting_And_InvariantValues()
    {
        var timestamp = new DateTimeOffset(2026, 7, 15, 12, 34, 56, TimeSpan.FromHours(-4));
        var message = new DetailedMessage(
            "right@-@value",
            "left",
            new NestedMessage("nested@-@value"),
            string.Empty,
            null,
            timestamp,
            1234.56m);
        var envelope = new Envelope<DetailedMessage>("Detailed", message);
        var serializer = new LengthPrefixedMessageSerializer();

        var bytes = serializer.Serialize(envelope);
        var result = serializer.Deserialize<Envelope<DetailedMessage>>(bytes);

        Assert.NotNull(result);
        Assert.Equal(envelope, result);
    }

    [Fact]
    public void None_Uses_Attribute_Order_And_Invariant_Primitive_Conversion()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var timestamp = new DateTimeOffset(2026, 7, 15, 12, 34, 56, TimeSpan.FromHours(2));
            var message = new LegacyMessage("second", "first", timestamp, 1234.56m);
            var envelope = new Envelope<LegacyMessage>("Legacy", message);
            var serializer = new NoneMessageSerializer();

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var bytes = serializer.Serialize(envelope);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var result = serializer.Deserialize<Envelope<LegacyMessage>>(bytes);

            Assert.NotNull(result);
            Assert.Equal(envelope, result);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [P2PMessage("Detailed")]
    public sealed record DetailedMessage(
        [property: P2PProperty(2)] string Right,
        [property: P2PProperty(1)] string Left,
        [property: P2PProperty(3)] NestedMessage Nested,
        [property: P2PProperty(4)] string Empty,
        [property: P2PProperty(5)] string? Optional,
        [property: P2PProperty(6)] DateTimeOffset Timestamp,
        [property: P2PProperty(7)] decimal Amount);

    public sealed record NestedMessage([property: P2PProperty(1)] string Value);

    [P2PMessage("Legacy")]
    public sealed record LegacyMessage(
        [property: P2PProperty(2)] string Second,
        [property: P2PProperty(1)] string First,
        [property: P2PProperty(3)] DateTimeOffset Timestamp,
        [property: P2PProperty(4)] decimal Amount);

    [Fact]
    public void Generic_P2PMessage_Uses_Referenced_Type_Name()
    {
        var attribute = new P2PMessageAttribute<GenericMessage>();

        Assert.Equal(nameof(GenericMessage), attribute.Name);
    }

    [Fact]
    public void LengthPrefixed_RoundTrips_Hub_Wire_Contracts()
    {
        var timestamp = new DateTimeOffset(2026, 8, 26, 12, 30, 0, TimeSpan.Zero);
        var message = new HubChatMessage(
            Guid.NewGuid(),
            42,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Alice",
            HubAudience.Team,
            Guid.NewGuid(),
            "Ready",
            timestamp);
        var envelope = new Envelope<HubChatMessage>("HubChatMessage", message);
        var serializer = new LengthPrefixedMessageSerializer();

        var result = serializer.Deserialize<Envelope<HubChatMessage>>(serializer.Serialize(envelope));

        Assert.Equal(envelope, result);
    }

    [P2PMessage<GenericMessage>]
    public sealed record GenericMessage([property: P2PProperty(1)] string Value);
}