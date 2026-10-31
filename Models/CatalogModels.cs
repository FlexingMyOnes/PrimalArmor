using System;
using System.Collections.Generic;
using System.Linq;

namespace PrimalArmor.Models;

public enum CatalogIssueSeverity { Warning, Error }

public sealed record CatalogIssue(string Code, string Message,
    CatalogIssueSeverity Severity = CatalogIssueSeverity.Warning,
    string? JobTag = null, string? Path = null)
{
    public override string ToString() => Message;
}

public sealed record ModVariant
{
    public required uint JobId { get; init; }
    public required string JobTag { get; init; }
    public string Job => JobTag;
    public required string Option { get; init; }
    public required string RaceSex { get; init; }
    public required string SourceDirectory { get; init; }
    /// <summary>Game path to absolute mod file, or a game path for FileSwaps.</summary>
    public required IReadOnlyDictionary<string, string> Files { get; init; }
    public bool Available { get; init; } = true;
    public string CacheKey => $"{JobTag}:{Option}";
}

public sealed record JobCatalog
{
    public required uint JobId { get; init; }
    public required string JobTag { get; init; }
    public string? SourceDirectory { get; init; }
    /// <summary>Explicit recipe race/sex keys, such as MidlanderMale.</summary>
    public required IReadOnlyDictionary<string, ModVariant> Variants { get; init; }
    public bool Available => Variants.Values.Any(v => v.Available);
}

public sealed record CatalogSnapshot
{
    public string ModRoot { get; init; } = string.Empty;
    public IReadOnlyDictionary<uint, JobCatalog> Jobs { get; init; }
        = new Dictionary<uint, JobCatalog>();
    /// <summary>ObjectType:PrimaryId:SecondaryId:Variant:EquipSlot:BodySlot.</summary>
    public IReadOnlySet<string> WhitelistImcKeys { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> CarrierPaths { get; init; } = new HashSet<string>();
    public IReadOnlyList<CatalogIssue> Issues { get; init; } = Array.Empty<CatalogIssue>();
    public ModVariant? FindVariant(uint jobId, string raceSex)
        => Jobs.TryGetValue(jobId, out var job) && job.Variants.TryGetValue(raceSex, out var variant)
            ? variant : null;
}

public sealed record CandidatePathSnapshot(
    IReadOnlySet<string> Paths, IReadOnlyList<CatalogIssue> Issues, bool Complete);
