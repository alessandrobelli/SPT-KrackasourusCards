using SPTarkov.Server.Core.Models.Spt.Mod;

namespace KrackKards;

// 4.1 replaced the AbstractModMetadata base record with the IModMetadata interface.
// IsBundleMod is gone: SPTStartupHostedService now decides a mod ships bundles by
// looking for bundles.json in the mod folder, which this mod already has.
public record ModMetadata : IModMetadata
{
    public string ModGuid  { get; init; } = "com.vonbraunz.krackkards";
    public string Name     { get; init; } = "KrackKards";
    public string Author   { get; init; } = "Vonbraunz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version    { get; init; } = new("1.0.0");
    public SemanticVersioning.Range   SptVersion { get; init; } = new("~4.1.2");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities  { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url    { get; init; }
    public string License { get; init; } = "NCSA Open Source";
}
