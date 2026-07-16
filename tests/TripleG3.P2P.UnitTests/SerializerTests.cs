using System.Globalization;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
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

    [UdpMessage("Detailed")]
    public sealed record DetailedMessage(
        [property: Udp(2)] string Right,
        [property: Udp(1)] string Left,
        [property: Udp(3)] NestedMessage Nested,
        [property: Udp(4)] string Empty,
        [property: Udp(5)] string? Optional,
        [property: Udp(6)] DateTimeOffset Timestamp,
        [property: Udp(7)] decimal Amount);

    public sealed record NestedMessage([property: Udp(1)] string Value);

    [UdpMessage("Legacy")]
    public sealed record LegacyMessage(
        [property: Udp(2)] string Second,
        [property: Udp(1)] string First,
        [property: Udp(3)] DateTimeOffset Timestamp,
        [property: Udp(4)] decimal Amount);
}