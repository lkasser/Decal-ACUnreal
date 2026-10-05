using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AC.Host.Overlay
{
    /// <summary>
    /// The one way these types are turned into JSON and back, so both ends of the pipe
    /// agree on the spelling.
    /// </summary>
    public static class OverlayJson
    {
        /// <summary>
        /// Snake case for anything not spelled out by an attribute, and no escaping of
        /// characters that are only dangerous in HTML.
        /// </summary>
        /// <remarks>
        /// The relaxed encoder is chosen because the reader is a JSON parser on the other
        /// side of a pipe, not a browser: escaping every accented character to \uXXXX
        /// would make the payload longer and a captured frame far harder to read, and buys
        /// nothing here. It also keeps the length prefix an honest count of the UTF-8
        /// bytes a name actually costs.
        /// </remarks>
        public static JsonSerializerOptions Options { get; } = BuildOptions();

        /// <summary>Serialises a snapshot for the wire.</summary>
        public static string ToJson(OverlayState state) => JsonSerializer.Serialize(state, Options);

        /// <summary>Serialises a command. Used by the overlay side and by tests.</summary>
        public static string ToJson(OverlayCommand command) => JsonSerializer.Serialize(command, Options);

        /// <summary>Serialises an image into the frame it travels in.</summary>
        public static string ToJson(OverlayImage image) => JsonSerializer.Serialize(new OverlayImageFrame { Image = image }, Options);

        /// <summary>Serialises the keys to hold into the frame they travel in.</summary>
        public static string ToJson(OverlayInput input) => JsonSerializer.Serialize(new OverlayInputFrame { Input = input }, Options);

        /// <summary>Reads an input frame back. Used by tests.</summary>
        public static OverlayInput ReadInput(string json) => JsonSerializer.Deserialize<OverlayInputFrame>(json, Options)?.Input;

        /// <summary>Reads an image frame back. Used by tests.</summary>
        public static OverlayImage ReadImage(string json) => JsonSerializer.Deserialize<OverlayImageFrame>(json, Options)?.Image;

        /// <summary>Reads a snapshot back. Throws <see cref="JsonException"/> on rubbish.</summary>
        public static OverlayState ReadState(string json) => JsonSerializer.Deserialize<OverlayState>(json, Options);

        /// <summary>Reads a command back. Throws <see cref="JsonException"/> on rubbish.</summary>
        public static OverlayCommand ReadCommand(string json) => JsonSerializer.Deserialize<OverlayCommand>(json, Options);

        private static JsonSerializerOptions BuildOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions(OverlayJsonContext.Default.Options)
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            options.MakeReadOnly();
            return options;
        }
    }

    /// <summary>
    /// The generated converters. Source generation rather than reflection because the
    /// host publishes several snapshots a second for the life of a session, and because
    /// it fails at compile time if one of these types grows a member that cannot cross
    /// the pipe.
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(OverlayState))]
    [JsonSerializable(typeof(OverlayCommand))]
    [JsonSerializable(typeof(OverlayImageFrame))]
    [JsonSerializable(typeof(OverlayInputFrame))]
    internal sealed partial class OverlayJsonContext : JsonSerializerContext
    {
    }
}
