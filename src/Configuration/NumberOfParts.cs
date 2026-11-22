using System;
using System.ComponentModel;
using System.Globalization;
using LectureExtraction.GoogleAi;
using Newtonsoft.Json;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Represents the segment partitioning policy for lecture extraction.
/// Can be either a fixed positive integer (e.g. 2, 3) or "auto", which dynamically determines
/// the number of parts based on the video duration and active model: older models split one part
/// per ~22.5 minutes (45 min -> 2, 90 min -> 4), while Gemini Hydrogen / Gemini 4 models split one
/// part per ~45 minutes (~45 min -> 1, ~90 min -> 2).
/// [Human] Bestimmt, in wie viele Teile ein Video geschnitten wird: entweder eine feste Zahl (z.B. 2) oder "auto",
/// wodurch die Teile anhand der Videolänge (und bei Hydrogen anhand von Long Decode) dynamisch berechnet werden.
/// </summary>
[TypeConverter(typeof(NumberOfPartsTypeConverter))]
[JsonConverter(typeof(NumberOfPartsNewtonsoftJsonConverter))]
[System.Text.Json.Serialization.JsonConverter(typeof(NumberOfPartsSystemTextJsonConverter))]
public readonly struct NumberOfParts : IEquatable<NumberOfParts>, IEquatable<int>, IComparable<NumberOfParts>, IComparable<int> {
    private readonly int _value; // 0 = Auto, > 0 = Fixed part count

    public const string AutoLiteral = "auto";
    public static NumberOfParts Auto => new(0);

    public NumberOfParts(int fixedParts) {
        if (fixedParts < 0) {
            throw new ArgumentOutOfRangeException(nameof(fixedParts), "NumberOfParts must be >= 1 or 'auto'.");
        }
        _value = fixedParts;
    }

    public bool IsAuto => _value == 0;
    public int? FixedParts => _value > 0 ? _value : null;

    /// <summary>
    /// Resolves the effective number of parts for a given video duration in seconds and optional model.
    /// Older models (or null/unspecified model) use the legacy 22.5 min/part rule (fallback 3 when
    /// duration is unknown); Gemini 4 / Hydrogen models use 45 min/part (fallback 2).
    /// </summary>
    public int Resolve(double durationSeconds = 0, string? modelName = null) {
        if (!IsAuto) {
            return _value;
        }
        return ResolveAuto(durationSeconds, modelName);
    }

    /// <summary>
    /// Legacy auto segment length for pre-Hydrogen models (2.5 / 3.x): two parts per 45-minute lecture
    /// unit keeps every segment around 25 minutes including overlap (45 min -> 2, 90 min -> 4).
    /// </summary>
    public const double AutoMinutesPerPart = 22.5;

    /// <summary>
    /// Hydrogen / Gemini 4 auto segment length: one part per 45-minute lecture unit runs a single
    /// Wednesday lecture in 1 pass and splits a ~90-minute Tuesday double lecture into 2 parts
    /// (boundaries at 67.5, 112.5, 157.5 min - covering 90 +/- 10 min with no 3-part band between 45 and 90 min).
    /// </summary>
    public const double HydrogenAutoMinutesPerPart = 45.0;

    /// <summary>
    /// Computes the auto-split part count from the duration in seconds and optional model name.
    /// Only Gemini 4 / Hydrogen models switch to the 45-minute single-pass / 2-part rule; all older
    /// models (and calls without a model name) retain the exact legacy 22.5-minute rule and 3-part fallback.
    /// </summary>
    public static int ResolveAuto(double durationSeconds, string? modelName = null) {
        bool useHydrogenRules = !string.IsNullOrWhiteSpace(modelName) && ModelCapabilities.SupportsLongDecode(modelName);
        if (durationSeconds <= 0) return useHydrogenRules ? 2 : 3;
        double minutes = durationSeconds / 60.0;
        double minutesPerPart = useHydrogenRules ? HydrogenAutoMinutesPerPart : AutoMinutesPerPart;
        return Math.Max(1, (int)Math.Round(minutes / minutesPerPart, MidpointRounding.AwayFromZero));
    }

    public static NumberOfParts Parse(string? text) {
        if (TryParse(text, out var result)) return result;
        throw new FormatException($"cannot convert '{text}' to {nameof(NumberOfParts)}. Expected a positive integer or 'auto'.");
    }

    public static bool TryParse(string? text, out NumberOfParts result) {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (string.Equals(text, AutoLiteral, StringComparison.OrdinalIgnoreCase)) {
            result = Auto;
            return true;
        }
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) && val >= 1) {
            result = new NumberOfParts(val);
            return true;
        }
        return false;
    }

    public static implicit operator NumberOfParts(int value) => new(value);
    public static implicit operator NumberOfParts(string value) => Parse(value);
    public static implicit operator int(NumberOfParts parts) => parts.Resolve();

    public bool Equals(NumberOfParts other) => _value == other._value;
    public bool Equals(int other) => !IsAuto && _value == other;

    public override bool Equals(object? obj) => obj switch {
        NumberOfParts other => Equals(other),
        int i => Equals(i),
        string s => TryParse(s, out var p) && Equals(p),
        _ => false
    };

    public override int GetHashCode() => _value.GetHashCode();

    public override string ToString() => IsAuto ? AutoLiteral : _value.ToString(CultureInfo.InvariantCulture);

    public int CompareTo(NumberOfParts other) => Resolve().CompareTo(other.Resolve());
    public int CompareTo(int other) => Resolve().CompareTo(other);

    public static bool operator ==(NumberOfParts left, NumberOfParts right) => left.Equals(right);
    public static bool operator !=(NumberOfParts left, NumberOfParts right) => !left.Equals(right);

    public static bool operator ==(NumberOfParts left, int right) => left.Equals(right);
    public static bool operator !=(NumberOfParts left, int right) => !left.Equals(right);

    public static bool operator ==(int left, NumberOfParts right) => right.Equals(left);
    public static bool operator !=(int left, NumberOfParts right) => !right.Equals(left);

    public static bool operator <=(NumberOfParts left, int right) => left.Resolve() <= right;
    public static bool operator >=(NumberOfParts left, int right) => left.Resolve() >= right;
    public static bool operator <(NumberOfParts left, int right) => left.Resolve() < right;
    public static bool operator >(NumberOfParts left, int right) => left.Resolve() > right;

    public static bool operator <=(int left, NumberOfParts right) => left <= right.Resolve();
    public static bool operator >=(int left, NumberOfParts right) => left >= right.Resolve();
    public static bool operator <(int left, NumberOfParts right) => left < right.Resolve();
    public static bool operator >(int left, NumberOfParts right) => left > right.Resolve();
}

public sealed class NumberOfPartsTypeConverter : TypeConverter {
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || sourceType == typeof(int) || sourceType == typeof(long) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) {
        if (value is string s) return NumberOfParts.Parse(s);
        if (value is int i) return new NumberOfParts(i);
        if (value is long l) return new NumberOfParts((int)l);
        return base.ConvertFrom(context, culture, value);
    }

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string) || destinationType == typeof(int) || base.CanConvertTo(context, destinationType);

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) {
        if (value is NumberOfParts parts) {
            if (destinationType == typeof(string)) return parts.ToString();
            if (destinationType == typeof(int)) return parts.Resolve();
        }
        return base.ConvertTo(context, culture, value, destinationType);
    }
}

public sealed class NumberOfPartsNewtonsoftJsonConverter : Newtonsoft.Json.JsonConverter {
    public override bool CanConvert(Type objectType) => objectType == typeof(NumberOfParts) || objectType == typeof(NumberOfParts?);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        if (value is NumberOfParts parts) {
            if (parts.IsAuto) {
                writer.WriteValue(NumberOfParts.AutoLiteral);
            }
            else {
                writer.WriteValue(parts.FixedParts ?? 3);
            }
        }
        else {
            writer.WriteNull();
        }
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) {
        if (reader.TokenType == JsonToken.Integer) {
            return new NumberOfParts(Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture));
        }
        if (reader.TokenType == JsonToken.String) {
            return NumberOfParts.Parse(reader.Value?.ToString());
        }
        if (reader.TokenType == JsonToken.Null) {
            return objectType == typeof(NumberOfParts?) ? (NumberOfParts?)null : NumberOfParts.Auto;
        }
        throw new JsonSerializationException($"Unexpected token {reader.TokenType} when parsing {nameof(NumberOfParts)}.");
    }
}

public sealed class NumberOfPartsSystemTextJsonConverter : System.Text.Json.Serialization.JsonConverter<NumberOfParts> {
    public override NumberOfParts Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number) {
            return new NumberOfParts(reader.GetInt32());
        }
        if (reader.TokenType == System.Text.Json.JsonTokenType.String) {
            return NumberOfParts.Parse(reader.GetString());
        }
        throw new System.Text.Json.JsonException($"Unexpected token {reader.TokenType} when parsing {nameof(NumberOfParts)}.");
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, NumberOfParts value, System.Text.Json.JsonSerializerOptions options) {
        if (value.IsAuto) {
            writer.WriteStringValue(NumberOfParts.AutoLiteral);
        }
        else {
            writer.WriteNumberValue(value.FixedParts ?? 3);
        }
    }
}
