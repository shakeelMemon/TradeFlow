using System.Text;

namespace TradeFlow.Fix;

/// <summary>
/// Splits a TCP byte stream into complete FIX messages. A message starts with "8=FIX" and ends
/// with the SOH that follows the "10=nnn" checksum field.
/// </summary>
public sealed class FixReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[8192];
    private readonly StringBuilder _pending = new();

    public async Task<string?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = TryExtract();
            if (message is not null)
            {
                return message;
            }

            var read = await stream.ReadAsync(_buffer, cancellationToken);
            if (read == 0)
            {
                return null; // connection closed
            }
            _pending.Append(Encoding.ASCII.GetString(_buffer, 0, read));
        }
    }

    /// <summary>Feeds raw text directly (used by tests).</summary>
    public void Append(string text) => _pending.Append(text);

    public string? TryExtract()
    {
        var text = _pending.ToString();
        var start = text.IndexOf("8=FIX", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        var trailer = text.IndexOf(FixMessage.Soh + "10=", start, StringComparison.Ordinal);
        if (trailer < 0)
        {
            return null;
        }
        var end = text.IndexOf(FixMessage.Soh, trailer + 1);
        if (end < 0)
        {
            return null;
        }
        var message = text.Substring(start, end - start + 1);
        _pending.Remove(0, end + 1);
        return message;
    }
}
