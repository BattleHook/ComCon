using System.Text.Json;
using System.Text.Json.Serialization;
using ComCon.Core.Models;

namespace ComCon.Core.Configuration;

/// <summary>Serializes schema-versioned settings; enum strings use exact lower-camel-case names.</summary>
public static class ConfigurationJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = ConfigurationValidator.Validate(settings);
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        return JsonSerializer.Serialize(settings, Options);
    }

    public static AppSettings Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        AppSettings settings;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = Options.MaxDepth });
            RejectDuplicateProperties(document.RootElement, "$");
            settings = document.RootElement.Deserialize<AppSettings>(Options)
                ?? throw new ConfigurationSerializationException("Configuration JSON must contain an object, not null.");
        }
        catch (JsonException exception)
        {
            throw new ConfigurationSerializationException(
                $"Configuration JSON is malformed or contains an unknown/invalid value: {exception.Message}",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new ConfigurationSerializationException(
                $"Configuration JSON contains an invalid null or argument value: {exception.Message}",
                exception);
        }

        var errors = ConfigurationValidator.Validate(settings);
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        return settings;
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException($"Duplicate property at {path}.{property.Name}.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = true,
            MaxDepth = 32
        };
        options.Converters.Add(new ExactEnumJsonConverter<AdapterKind>());
        options.Converters.Add(new ExactEnumJsonConverter<FramingMode>());
        options.Converters.Add(new ExactEnumJsonConverter<SerialParity>());
        options.Converters.Add(new ExactEnumJsonConverter<SerialStopBits>());
        options.Converters.Add(new ExactEnumJsonConverter<SerialHandshake>());
        return options;
    }

    private sealed class ExactEnumJsonConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly IReadOnlyDictionary<string, TEnum> ValuesByName =
            Enum.GetValues<TEnum>().ToDictionary(
                value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()),
                value => value,
                StringComparer.Ordinal);

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Expected a lower-camel-case {typeof(TEnum).Name} string.");
            }

            var name = reader.GetString();
            if (name is null || !ValuesByName.TryGetValue(name, out var value))
            {
                throw new JsonException($"Unknown {typeof(TEnum).Name} value '{name}'.");
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value))
            {
                throw new JsonException($"Unsupported {typeof(TEnum).Name} value '{value}'.");
            }

            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
        }
    }
}
