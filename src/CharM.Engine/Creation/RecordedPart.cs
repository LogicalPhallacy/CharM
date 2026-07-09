namespace CharM.Engine.Creation;

/// <summary>
/// One part layer a character was built with, recorded for later auditing
/// against the rules database the character is opened under. Captured at build
/// time and persisted in the forward-compatible CharM extensions block.
/// </summary>
public sealed record RecordedPart(string PartId, string? Version, string? Category);
