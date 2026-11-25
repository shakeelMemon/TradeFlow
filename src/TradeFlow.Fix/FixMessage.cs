using System.Globalization;

namespace TradeFlow.Fix;

/// <summary>An ordered list of tag=value fields. Header fields are added by <see cref="FixCodec"/>.</summary>
public sealed class FixMessage
{
    public const char Soh = '\u0001';

    private readonly List<KeyValuePair<int, string>> _fields = new();

    public IReadOnlyList<KeyValuePair<int, string>> Fields => _fields;

    public string MsgType => Get(Tags.MsgType) ?? "";

    public FixMessage Set(int tag, string value)
    {
        var index = _fields.FindIndex(f => f.Key == tag);
        var field = new KeyValuePair<int, string>(tag, value);
        if (index >= 0)
        {
            _fields[index] = field;
        }
        else
        {
            _fields.Add(field);
        }
        return this;
    }

    public FixMessage Set(int tag, decimal value) => Set(tag, value.ToString(CultureInfo.InvariantCulture));

    public FixMessage Set(int tag, int value) => Set(tag, value.ToString(CultureInfo.InvariantCulture));

    public string? Get(int tag)
    {
        foreach (var field in _fields)
        {
            if (field.Key == tag)
            {
                return field.Value;
            }
        }
        return null;
    }

    public decimal GetDecimal(int tag) =>
        decimal.TryParse(Get(tag), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0m;

    public int GetInt(int tag) =>
        int.TryParse(Get(tag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>Parses a raw tag=value message (fields separated by SOH).</summary>
    public static FixMessage Parse(string raw)
    {
        var message = new FixMessage();
        foreach (var part in raw.Split(Soh, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            if (int.TryParse(part.AsSpan(0, eq), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tag))
            {
                message._fields.Add(new KeyValuePair<int, string>(tag, part.Substring(eq + 1)));
            }
        }
        return message;
    }
}
