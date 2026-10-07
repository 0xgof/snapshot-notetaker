using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using SnapshotNotetaker.Rendering;
using SnapshotNotetaker.Settings;

namespace SnapshotNotetaker.IO;

/// <summary>
/// Compile-time JSON metadata for the files the app reads at startup and while scanning the library;
/// avoids the reflection warm-up System.Text.Json otherwise pays on first use (~0.3 s).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SnapshotFile))]
internal sealed partial class JsonContext : JsonSerializerContext
{
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            TypeInfoResolver = JsonContext.Default,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new ColorConverter());
        options.Converters.Add(new PointConverter());
        options.Converters.Add(new VectorConverter());
        options.Converters.Add(new RectConverter());
        return options;
    }

    private sealed class ColorConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => ColorUtil.TryParse(reader.GetString(), out var c) ? c : Colors.Black;

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
            => writer.WriteStringValue(ColorUtil.ToHex(value));
    }

    private sealed class PointConverter : JsonConverter<Point>
    {
        public override Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = ReadNumbers(ref reader, 2);
            return new Point(v[0], v[1]);
        }

        public override void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options)
            => WriteNumbers(writer, value.X, value.Y);
    }

    private sealed class VectorConverter : JsonConverter<Vector>
    {
        public override Vector Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = ReadNumbers(ref reader, 2);
            return new Vector(v[0], v[1]);
        }

        public override void Write(Utf8JsonWriter writer, Vector value, JsonSerializerOptions options)
            => WriteNumbers(writer, value.X, value.Y);
    }

    private sealed class RectConverter : JsonConverter<Rect>
    {
        public override Rect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = ReadNumbers(ref reader, 4);
            return new Rect(v[0], v[1], Math.Max(0, v[2]), Math.Max(0, v[3]));
        }

        public override void Write(Utf8JsonWriter writer, Rect value, JsonSerializerOptions options)
        {
            if (value.IsEmpty) WriteNumbers(writer, 0, 0, 0, 0);
            else WriteNumbers(writer, value.X, value.Y, value.Width, value.Height);
        }
    }

    private static double[] ReadNumbers(ref Utf8JsonReader reader, int count)
    {
        var values = new double[count];
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an array of numbers.");
        int i = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            double v = reader.GetDouble();
            if (i < count) values[i] = v;
            i++;
        }
        return values;
    }

    private static void WriteNumbers(Utf8JsonWriter writer, params double[] values)
    {
        writer.WriteStartArray();
        foreach (var v in values) writer.WriteNumberValue(Math.Round(v, 2));
        writer.WriteEndArray();
    }
}
