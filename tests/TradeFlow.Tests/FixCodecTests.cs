using System.Text;
using TradeFlow.Fix;

namespace TradeFlow.Tests;

public class FixCodecTests
{
    private static string Encode(FixMessage body) =>
        FixCodec.Encode(MsgTypes.NewOrderSingle, body, "TRADEFLOW", "BROKER", 7, new DateTime(2026, 10, 15, 14, 30, 0, DateTimeKind.Utc));

    private static FixMessage SampleOrder() => new FixMessage()
        .Set(Tags.ClOrdID, "abc123")
        .Set(Tags.Symbol, "CAN-2.75-2035")
        .Set(Tags.Side, "1")
        .Set(Tags.OrderQty, 3_000_000m)
        .Set(Tags.Price, 97.48m);

    [Fact]
    public void Encoded_message_has_correct_header_body_length_and_checksum()
    {
        var raw = Encode(SampleOrder());

        Assert.StartsWith("8=FIX.4.4\u00019=", raw);
        Assert.Contains("\u000135=D\u000149=TRADEFLOW\u000156=BROKER\u000134=7\u000152=20261015-14:30:00.000\u0001", raw);
        Assert.Matches("\u000110=\\d{3}\u0001$", raw);
        Assert.True(FixCodec.HasValidChecksum(raw));
        Assert.True(FixCodec.HasValidBodyLength(raw));
    }

    [Fact]
    public void Checksum_is_sum_of_bytes_mod_256()
    {
        var raw = Encode(SampleOrder());
        var trailer = raw.LastIndexOf("\u000110=", StringComparison.Ordinal);
        var expected = Encoding.ASCII.GetBytes(raw[..(trailer + 1)]).Sum(b => (int)b) % 256;
        Assert.Equal(expected.ToString("D3"), raw.Substring(trailer + 4, 3));
    }

    [Fact]
    public void Tampered_message_fails_checksum()
    {
        var raw = Encode(SampleOrder()).Replace("97.48", "99.48");
        Assert.False(FixCodec.HasValidChecksum(raw));
    }

    [Fact]
    public void Parse_round_trips_fields()
    {
        var parsed = FixMessage.Parse(Encode(SampleOrder()));
        Assert.Equal("D", parsed.MsgType);
        Assert.Equal("abc123", parsed.Get(Tags.ClOrdID));
        Assert.Equal(3_000_000m, parsed.GetDecimal(Tags.OrderQty));
        Assert.Equal(97.48m, parsed.GetDecimal(Tags.Price));
        Assert.Equal(7, parsed.GetInt(Tags.MsgSeqNum));
    }

    [Fact]
    public void Reader_splits_a_stream_into_messages_even_when_split_across_reads()
    {
        var first = Encode(SampleOrder());
        var second = FixCodec.Encode(MsgTypes.Heartbeat, new FixMessage(), "BROKER", "TRADEFLOW", 8, DateTime.UtcNow);
        var reader = new FixReader(Stream.Null);

        reader.Append(first[..20]);
        Assert.Null(reader.TryExtract());
        reader.Append(first[20..] + second[..10]);
        Assert.Equal(first, reader.TryExtract());
        Assert.Null(reader.TryExtract());
        reader.Append(second[10..]);
        Assert.Equal(second, reader.TryExtract());
    }
}
