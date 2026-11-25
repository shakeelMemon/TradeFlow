using System.Globalization;
using System.Text;

namespace TradeFlow.Fix;

/// <summary>
/// Builds and checks FIX 4.4 wire messages:
/// 8=FIX.4.4|9=BodyLength|35=..|49=..|56=..|34=..|52=..|body..|10=CheckSum|
/// BodyLength counts the bytes from tag 35 up to the SOH before tag 10.
/// CheckSum is the sum of every byte before tag 10, modulo 256, as three digits.
/// </summary>
public static class FixCodec
{
    public const string BeginString = "FIX.4.4";

    private static readonly HashSet<int> HeaderTags = new()
    {
        Tags.BeginString, Tags.BodyLength, Tags.MsgType, Tags.SenderCompID,
        Tags.TargetCompID, Tags.MsgSeqNum, Tags.SendingTime, Tags.CheckSum,
    };

    public static string Encode(string msgType, FixMessage body, string senderCompId, string targetCompId,
        int msgSeqNum, DateTime sendingTimeUtc)
    {
        var sb = new StringBuilder();
        Append(sb, Tags.MsgType, msgType);
        Append(sb, Tags.SenderCompID, senderCompId);
        Append(sb, Tags.TargetCompID, targetCompId);
        Append(sb, Tags.MsgSeqNum, msgSeqNum.ToString(CultureInfo.InvariantCulture));
        Append(sb, Tags.SendingTime, FormatTime(sendingTimeUtc));
        foreach (var field in body.Fields)
        {
            if (!HeaderTags.Contains(field.Key))
            {
                Append(sb, field.Key, field.Value);
            }
        }

        var bodyText = sb.ToString();
        var withoutTrailer = "8=" + BeginString + FixMessage.Soh
            + "9=" + Encoding.ASCII.GetByteCount(bodyText).ToString(CultureInfo.InvariantCulture) + FixMessage.Soh
            + bodyText;
        return withoutTrailer + "10=" + Checksum(withoutTrailer).ToString("D3", CultureInfo.InvariantCulture) + FixMessage.Soh;
    }

    public static int Checksum(string text)
    {
        var sum = 0;
        foreach (var b in Encoding.ASCII.GetBytes(text))
        {
            sum += b;
        }
        return sum % 256;
    }

    public static bool HasValidChecksum(string raw)
    {
        var trailer = raw.LastIndexOf(FixMessage.Soh + "10=", StringComparison.Ordinal);
        if (trailer < 0 || raw.Length < trailer + 7)
        {
            return false;
        }
        var digits = raw.Substring(trailer + 4, 3);
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected)
            && expected == Checksum(raw.Substring(0, trailer + 1));
    }

    public static bool HasValidBodyLength(string raw)
    {
        var message = FixMessage.Parse(raw);
        var declared = message.GetInt(Tags.BodyLength);
        var start = raw.IndexOf(FixMessage.Soh + "35=", StringComparison.Ordinal);
        var trailer = raw.LastIndexOf(FixMessage.Soh + "10=", StringComparison.Ordinal);
        if (start < 0 || trailer < 0)
        {
            return false;
        }
        return Encoding.ASCII.GetByteCount(raw.Substring(start + 1, trailer - start)) == declared;
    }

    public static string FormatTime(DateTime utc) =>
        utc.ToString("yyyyMMdd-HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Human-readable form for logs: SOH shown as '|'.</summary>
    public static string Pretty(string raw) => raw.Replace(FixMessage.Soh, '|');

    private static void Append(StringBuilder sb, int tag, string value) =>
        sb.Append(tag.ToString(CultureInfo.InvariantCulture)).Append('=').Append(value).Append(FixMessage.Soh);
}
