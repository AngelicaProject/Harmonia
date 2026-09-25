using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Harmonia.Packs.Hpk;

public enum HpkContentPolicy
{
    Reviewed,
    All,
}

public sealed record HpkCounts(long Sheets, long Rows, long Cells, long ReviewedCells, long Strings);

// The MANIFEST section. Parsing is strict: the pack is a compiled artifact,
// so an unexpected or missing field means a writer bug or tampering, never a
// vendor extension.
public sealed partial class HpkManifest
{
    public string PackId { get; private init; } = string.Empty;
    public string Title { get; private init; } = string.Empty;
    public string PublisherName { get; private init; } = string.Empty;
    public string? PublisherUrl { get; private init; }
    public string? License { get; private init; }
    public long Sequence { get; private init; }
    public string Version { get; private init; } = string.Empty;
    public string Channel { get; private init; } = string.Empty;
    public string TargetLanguage { get; private init; } = string.Empty;
    public string SourceLanguage { get; private init; } = string.Empty;
    public string SourceGameVersion { get; private init; } = string.Empty;
    public string SourceContentId { get; private init; } = string.Empty;
    public string SourceSnapshotId { get; private init; } = string.Empty;
    public HpkContentPolicy ContentPolicy { get; private init; }
    public string ProjectCommit { get; private init; } = string.Empty;
    public string ExporterAeria { get; private init; } = string.Empty;
    public string ExporterAtlas { get; private init; } = string.Empty;
    public Version MinHarmonia { get; private init; } = new(0, 0);
    public HpkCounts Counts { get; private init; } = new(0, 0, 0, 0, 0);

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

        Fields(root, "manifest", "packId", "title", "publisher", "license", "release", "target", "source",
            "contentPolicy", "project", "exporter", "minHarmonia", "counts");
        var publisher = Obj(root, "publisher", "name", "url");
        var release = Obj(root, "release", "sequence", "version", "channel");
        var target = Obj(root, "target", "language");
        var source = Obj(root, "source", "language", "gameVersion", "contentId", "snapshotId");
        var project = Obj(root, "project", "commit");
        var exporter = Obj(root, "exporter", "aeria", "atlas");
        var counts = Obj(root, "counts", "sheets", "rows", "cells", "reviewedCells", "strings");

        var packId = Str(root, "packId");
        if (!PackIdPattern().IsMatch(packId))
            throw new HpkFormatException("Invalid packId.");

        var channel = Str(release, "channel");
        if (channel is not ("stable" or "testing"))
            throw new HpkFormatException("Invalid release.channel.");

        var policy = Str(root, "contentPolicy") switch
        {
            "reviewed" => HpkContentPolicy.Reviewed,
            "all" => HpkContentPolicy.All,
            _ => throw new HpkFormatException("Invalid contentPolicy."),
        };

        var sequence = Int(release, "sequence");
        if (sequence < 1)
            throw new HpkFormatException("release.sequence must be positive.");

        if (!System.Version.TryParse(Str(root, "minHarmonia"), out var minHarmonia))
            throw new HpkFormatException("Invalid minHarmonia.");

        var commit = Str(project, "commit");
        if (!HexPattern().IsMatch(commit) || commit.Length != 40)
            throw new HpkFormatException("Invalid project.commit.");

        return new HpkManifest
        {
            PackId = packId,
            Title = Str(root, "title"),
            PublisherName = Str(publisher, "name"),
            PublisherUrl = OptStr(publisher, "url"),
            License = OptStr(root, "license"),
            Sequence = sequence,
            Version = Str(release, "version"),
            Channel = channel,
            TargetLanguage = Str(target, "language"),
            SourceLanguage = Str(source, "language"),
            SourceGameVersion = Str(source, "gameVersion"),
            SourceContentId = Hash(source, "contentId"),
            SourceSnapshotId = Hash(source, "snapshotId"),
            ContentPolicy = policy,
            ProjectCommit = commit,
            ExporterAeria = Str(exporter, "aeria"),
            ExporterAtlas = Str(exporter, "atlas"),
            MinHarmonia = minHarmonia,
            Counts = new HpkCounts(
                Int(counts, "sheets"), Int(counts, "rows"), Int(counts, "cells"),
                Int(counts, "reviewedCells"), Int(counts, "strings")),
        };
    }

    private static void Fields(JObject obj, string name, params string[] expected)
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

    private static JObject Obj(JObject parent, string field, params string[] expected)
    {
        if (parent[field] is not JObject obj)
            throw new HpkFormatException($"Field '{field}' must be an object.");

        Fields(obj, field, expected);
        return obj;
    }

    private static string Str(JObject obj, string field)
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

    private static long Int(JObject obj, string field)
    {
        if (obj[field] is not JValue { Type: JTokenType.Integer } value)
            throw new HpkFormatException($"Field '{field}' must be an integer.");

        var number = (long)value;
        if (number < 0)
            throw new HpkFormatException($"Field '{field}' must not be negative.");

        return number;
    }

    private static string Hash(JObject obj, string field)
    {
        var value = Str(obj, field);
        if (!value.StartsWith("sha256:", StringComparison.Ordinal) || value.Length != 71 || !HexPattern().IsMatch(value[7..]))
            throw new HpkFormatException($"Field '{field}' must be a sha256 identity.");

        return value;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex PackIdPattern();

    [GeneratedRegex("^[0-9a-f]+$")]
    private static partial Regex HexPattern();
}
