using System.Text.Json.Serialization;

namespace CharM.RulesDb.Import;

/// <summary>
/// Source-generated JSON metadata for the part-management POCOs persisted to
/// disk (the layer manifest, the remote-source config, and the enabled-source
/// list). Using a <see cref="JsonSerializerContext"/> instead of reflection-based
/// serialization keeps these trim-safe and avoids runtime reflection over the
/// types.
///
/// <para>NOTE: the rules-element JSON columns (<c>List&lt;RuleDirective&gt;</c>,
/// <c>Dictionary&lt;string,string&gt;</c>, <c>List&lt;KeyValuePair&gt;</c>) are
/// deliberately NOT here yet — they sit on the hot DB load/build path and
/// interact with the hand-written <c>RuleDirectiveJsonConverter</c>, so they are
/// left reflection-based until the Mac Catalyst <c>MtouchLink</c> trimming work
/// is actually attempted and can be validated on that platform.</para>
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PartManifest))]
[JsonSerializable(typeof(PartManifestEntry))]
[JsonSerializable(typeof(PartSourceConfig))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class RulesDbJsonContext : JsonSerializerContext
{
}
