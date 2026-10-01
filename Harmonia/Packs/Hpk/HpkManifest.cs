using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Harmonia.Packs.Hpk;

// The MANIFEST section. Parsing is strict: the pack is a compiled artifact,
// so an unexpected or missing field means a writer bug or tampering, never a
// vendor extension.
public sealed partial class HpkManifest
{
    public string Title { get; private init; } = string.Empty;
    public string TeamName { get; private init; } = string.Empty;
    public string? TeamUrl { get; private init; }
    public IReadOnlyList<string> Authors { get; private init; } = [];
    public string? License { get; private init; }

    // YYYY.MM.DD.NNNN; see PackVersion.
    public string Version { get; private init; } = string.Empty;
    public string Channel { get; private init; } = string.Empty;

    // The language of the translations.
    public string Language { get; private init; } = string.Empty;

    // The client language the pack translates from, and the game build it
    // was made for.
    public string GameLanguage { get; private init; } = string.Empty;
    public string GameVersion { get; private init; } = string.Empty;
    public string Aeria { get; private init; } = string.Empty;
    public string Commit { get; private init; } = string.Empty;
    public Version MinHarmonia { get; private init; } = new(0, 0);

    public static HpkManifest Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > HpkFormat.MaxManifestBytes)
            throw new HpkFormatException("Manifest is too large.");
        if (utf8.StartsWith("﻿"u8))
            throw new HpkFormatException("Manifest must not start with a BOM.");

        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            throw new HpkFormatException("Manifest is not valid UTF-8.");
        }

        JObject root;
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read())
                throw new HpkFormatException("Manifest has trailing content.");
        }
        catch (JsonException ex)
        {
            throw new HpkFormatException("Manifest is not valid JSON: " + ex.Message);
        }

        Fields(root, "manifest", "title", "team", "authors", "license", "version", "channel", "language",
            "game", "built", "minHarmonia");
        var team = Obj(root, "team", "name", "url");
        var game = Obj(root, "game", "language", "version");
        var built = Obj(root, "built", "aeria", "commit");

        var version = Str(root, "version");
        if (!PackVersion.IsValid(version))
            throw new HpkFormatException("Invalid version.");

        var channel = Str(root, "channel");
        if (channel is not ("stable" or "testing"))
            throw new HpkFormatException("Invalid channel.");

        if (!System.Version.TryParse(Str(root, "minHarmonia"), out var minHarmonia))
            throw new HpkFormatException("Invalid minHarmonia.");

        var commit = Str(built, "commit");
        if (!HexPattern().IsMatch(commit) || commit.Length != 40)
            throw new HpkFormatException("Invalid built.commit.");

        if (root["authors"] is not JArray authorList)
            throw new HpkFormatException("Field 'authors' must be an array.");
        var authors = new List<string>();
        foreach (var author in authorList)
        {
            if (author is not JValue { Type: JTokenType.String } value || string.IsNullOrWhiteSpace((string?)value))
                throw new HpkFormatException("Every author must be a non-empty string.");
            var name = (string)value!;
            if (authors.Contains(name, StringComparer.Ordinal))
                throw new HpkFormatException("An author is listed twice.");
            authors.Add(name);
        }

        return new HpkManifest
        {
            Title = Str(root, "title"),
            TeamName = Str(team, "name"),
            TeamUrl = OptStr(team, "url"),
            Authors = authors,
            License = OptStr(root, "license"),
            Version = version,
            Channel = channel,
            Language = Str(root, "language"),
            GameLanguage = Str(game, "language"),
            GameVersion = Str(game, "version"),
            Aeria = Str(built, "aeria"),
            Commit = commit,
            MinHarmonia = minHarmonia,
        };
    }

    internal static void Fields(JObject obj, string name, params string[] expected)
    {
        foreach (var property in obj.Properties())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                throw new HpkFormatException($"Unknown field '{property.Name}' in {name}.");
        }

        foreach (var field in expected)
        {
            if (obj.Property(field, StringComparison.Ordinal) is null)
                throw new HpkFormatException($"Missing field '{field}' in {name}.");
        }
    }

    internal static JObject Obj(JObject parent, string field, params string[] expected)
    {
        if (parent[field] is not JObject obj)
            throw new HpkFormatException($"Field '{field}' must be an object.");

        Fields(obj, field, expected);
        return obj;
    }

    internal static string Str(JObject obj, string field)
    {
        if (obj[field] is not JValue { Type: JTokenType.String } value || string.IsNullOrWhiteSpace((string?)value))
            throw new HpkFormatException($"Field '{field}' must be a non-empty string.");

        return (string)value!;
    }

    private static string? OptStr(JObject obj, string field)
    {
        var token = obj[field];
        if (token is null || token.Type == JTokenType.Null)
            return null;

        return Str(obj, field);
    }

    internal static long Int(JObject obj, string field)
    {
        if (obj[field] is not JValue { Type: JTokenType.Integer } value)
            throw new HpkFormatException($"Field '{field}' must be an integer.");

        var number = (long)value;
        if (number < 0)
            throw new HpkFormatException($"Field '{field}' must not be negative.");

        return number;
    }

    [GeneratedRegex("^[0-9a-f]+$")]
    private static partial Regex HexPattern();
}
