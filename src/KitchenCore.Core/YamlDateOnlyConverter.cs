using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KitchenCore.Core;

/// <summary>
/// Reads and writes <see cref="DateOnly"/> as a plain <c>2026-10-14</c>.
///
/// YamlDotNet has no idea what a DateOnly is: left to itself it serializes one
/// as an object with year, month, day, dayOfWeek, dayOfYear and dayNumber --
/// six lines of noise in a hand-editable file -- and reads that object back as
/// 0001-01-01, so every date written that way was silently lost.
/// </summary>
public sealed class YamlDateOnlyConverter : IYamlTypeConverter
{
    public static readonly YamlDateOnlyConverter Instance = new();

    public bool Accepts(Type type) => type == typeof(DateOnly) || type == typeof(DateOnly?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        // An old file may still hold the object form; skip it rather than fail.
        if (parser.TryConsume<MappingStart>(out _))
        {
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                parser.SkipThisAndNestedEvents();
            }

            return type == typeof(DateOnly) ? default(DateOnly) : null;
        }

        var scalar = parser.Consume<Scalar>();

        if (DateOnly.TryParseExact(scalar.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        return type == typeof(DateOnly) ? default(DateOnly) : null;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var text = value is DateOnly date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
        emitter.Emit(new Scalar(text));
    }
}
