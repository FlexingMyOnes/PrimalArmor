using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PrimalArmor.Models;

namespace PrimalArmor.Catalog;

/// <summary>Reads local Penumbra data without changing any mod or collection.</summary>
public sealed class ModCatalog
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions SerializerOptions = new()
        { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true,
          ReadCommentHandling = JsonCommentHandling.Skip };
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private readonly string? recipesPath;
    private LoadLocation? location;

    /// <param name="recipesPath">Optional explicit recipe file, useful for standalone tests.</param>
    public ModCatalog(string? recipesPath = null) => this.recipesPath = recipesPath;

    /// <param name="pluginConfigsDir">Parent of the plugin's own config directory.</param>
    public Task<CatalogSnapshot> LoadAsync(string pluginConfigsDir, CancellationToken ct = default)
        => LoadAsync(pluginConfigsDir, null, ct);

    /// <param name="modDirectoryOverride">Optional absolute mod root; blank uses Penumbra's configured directory.</param>
    public Task<CatalogSnapshot> LoadAsync(string pluginConfigsDir, string? modDirectoryOverride, CancellationToken ct = default)
        => Task.Run(() => Load(pluginConfigsDir, modDirectoryOverride, ct), ct);

    public async Task<IReadOnlySet<string>> GetCandidatePaths(Guid collectionId, CancellationToken ct = default)
        => (await GetCandidatePathSnapshotAsync(collectionId, ct).ConfigureAwait(false)).Paths;

    /// <summary>All keys from every option of each effectively enabled mod, including inherited settings.</summary>
    public Task<CandidatePathSnapshot> GetCandidatePathSnapshotAsync(Guid collectionId, CancellationToken ct = default)
    {
        var current = Volatile.Read(ref location);
        return Task.Run(() => GetCandidates(current, collectionId, ct), ct);
    }

    private CatalogSnapshot Load(string pluginConfigsDir, string? modDirectoryOverride, CancellationToken ct)
    {
        var issues = new List<CatalogIssue>();
        var recipes = ReadRecipes(issues);
        var jobs = recipes.Jobs.ToDictionary(j => j.JobId, j => new JobCatalog
        {
            JobId = j.JobId, JobTag = j.JobTag,
            Variants = new Dictionary<string, ModVariant>(StringComparer.OrdinalIgnoreCase),
        });
        var whitelist = new HashSet<string>(StringComparer.Ordinal);
        var carriers = new HashSet<string>(Paths);
        var modRoot = string.Empty;
        var manualDirectory = !string.IsNullOrWhiteSpace(modDirectoryOverride);
        if (manualDirectory)
        {
            modRoot = modDirectoryOverride!.Trim();
            if (modRoot.Length >= 2 && ((modRoot[0] == '"' && modRoot[^1] == '"')
                || (modRoot[0] == '\'' && modRoot[^1] == '\'')))
                modRoot = modRoot[1..^1].Trim();
        }
        else
        {
            var configPath = Path.Combine(pluginConfigsDir, "Penumbra.json");
            using var config = ReadJson(configPath, issues, "PenumbraConfig");
            if (config is not null) modRoot = Text(config.RootElement, "ModDirectory");
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(modRoot))
            {
                if (!Path.IsPathFullyQualified(modRoot))
                    throw new ArgumentException("The mod directory must be an absolute path.");
                modRoot = Path.GetFullPath(modRoot);
            }
            else if (manualDirectory)
                throw new ArgumentException("The selected mod directory is empty.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(new("ModDirectoryInvalid", $"Penumbra's mod directory is invalid: {ex.Message}",
                CatalogIssueSeverity.Error, Path: modRoot));
            AddMissingJobs(recipes, jobs, issues);
            Volatile.Write(ref location, new LoadLocation(pluginConfigsDir, string.Empty));
            return Snapshot();
        }
        if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
        {
            issues.Add(new("ModDirectoryMissing", "Penumbra's mod directory is unavailable.",
                CatalogIssueSeverity.Error, Path: modRoot));
            AddMissingJobs(recipes, jobs, issues);
            Volatile.Write(ref location, new LoadLocation(pluginConfigsDir, string.Empty));
            return Snapshot();
        }
        var installed = new List<InstalledMod>();
        foreach (var directory in EnumerateDirectories(modRoot, issues))
        {
            ct.ThrowIfCancellationRequested();
            var metaPath = Path.Combine(directory, "meta.json");
            if (!File.Exists(metaPath)) continue;
            using var meta = ReadJson(metaPath, issues, "ModMetadata");
            if (meta is null) continue;
            var name = Text(meta.RootElement, "Name");
            if (!name.Contains("The Primal", StringComparison.OrdinalIgnoreCase)) continue;
            var recipe = recipes.Jobs.FirstOrDefault(j => Regex.IsMatch(name,
                $@"\b{Regex.Escape(j.JobTag)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            if (recipe is null) continue;
            var swapped = name.Contains("Swapped", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(directory).Contains("Swapped", StringComparison.OrdinalIgnoreCase);
            var documents = new List<ModDocument>();
            foreach (var file in ModJsonFiles(directory, issues))
            {
                ct.ThrowIfCancellationRequested();
                using var doc = ReadJson(file, issues, "ModData", recipe.JobTag);
                if (doc is null) continue;
                var root = doc.RootElement.Clone();
                documents.Add(new(file, root));
                CollectHeadCarriers(root, whitelist, carriers);
            }
            installed.Add(new(directory, recipe.JobTag, swapped, documents));
        }

        foreach (var recipe in recipes.Jobs)
        {
            ct.ThrowIfCancellationRequested();
            // Prefer a complete installed version; never source assets from item-swapped copies.
            var sources = installed.Where(p => p.JobTag == recipe.JobTag && !p.Swapped)
                .Select(p => (Mod: p, Group: p.Documents.FirstOrDefault(d =>
                    NameEquals(Text(d.Root, "Name"), recipe.Group))))
                .OrderByDescending(p => CountMatchingOptions(p.Group?.Root, recipe))
                .ThenByDescending(p => Path.GetFileName(p.Mod.Directory), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sources.Length == 0) continue;
            if (sources.Length > 1)
                issues.Add(new("MultipleSources", $"{recipe.JobTag}: several source mods found; using {Path.GetFileName(sources[0].Mod.Directory)}.", JobTag: recipe.JobTag));
            var source = sources[0];
            var variants = new Dictionary<string, ModVariant>(StringComparer.OrdinalIgnoreCase);
            if (source.Group is null)
                issues.Add(new("GroupMissing", $"{recipe.JobTag}: option group '{recipe.Group}' was not found.",
                    CatalogIssueSeverity.Error, recipe.JobTag, source.Mod.Directory));
            else
            {
                var options = Array(source.Group.Root, "Options").ToArray();
                var optionCache = new Dictionary<string, (Dictionary<string, string> Files, bool Available)>(StringComparer.OrdinalIgnoreCase);
                foreach (var race in recipe.Races)
                {
                    if (!optionCache.TryGetValue(race.Option, out var mapping))
                    {
                        var matches = options.Where(o => NameEquals(Text(o, "Name"), race.Option)).ToArray();
                        if (matches.Length != 1)
                        {
                            issues.Add(new("OptionMissing", $"{recipe.JobTag}: expected one option '{race.Option}', found {matches.Length}.",
                                CatalogIssueSeverity.Error, recipe.JobTag, source.Group.File));
                            mapping = (new Dictionary<string, string>(Paths), false);
                        }
                        else
                            mapping = ReadVariantFiles(matches[0], source.Mod.Directory, race,
                                recipes.EmperorEffectPath, recipe.JobTag, issues);
                        optionCache[race.Option] = mapping;
                    }
                    variants[race.RaceSex] = new ModVariant
                    {
                        JobId = recipe.JobId, JobTag = recipe.JobTag, RaceSex = race.RaceSex,
                        Option = race.Option, SourceDirectory = source.Mod.Directory,
                        Files = mapping.Files, Available = mapping.Available,
                    };
                }
            }
            jobs[recipe.JobId] = new JobCatalog { JobId = recipe.JobId, JobTag = recipe.JobTag,
                SourceDirectory = source.Mod.Directory, Variants = variants };
        }
        AddMissingJobs(recipes, jobs, issues);
        Volatile.Write(ref location, new LoadLocation(pluginConfigsDir, modRoot));
        return Snapshot();

        CatalogSnapshot Snapshot() => new()
        {
            ModRoot = modRoot, Jobs = jobs, WhitelistImcKeys = whitelist,
            CarrierPaths = carriers, Issues = issues,
        };
    }

    private static (Dictionary<string, string> Files, bool Available) ReadVariantFiles(JsonElement option,
        string directory, RaceRecipe race, string emperor, string job, List<CatalogIssue> issues)
    {
        var files = new Dictionary<string, string>(Paths);
        var available = true;
        if (Property(option, "Files", out var mappings) && mappings.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in mappings.EnumerateObject())
            {
                if (file.Value.ValueKind != JsonValueKind.String) { available = false; continue; }
                var relative = file.Value.GetString() ?? string.Empty;
                try
                {
                    var full = Path.GetFullPath(Path.Combine(directory, relative.Replace('\\', Path.DirectorySeparatorChar)));
                    if (string.IsNullOrWhiteSpace(relative) || !File.Exists(full))
                    {
                        available = false;
                        issues.Add(new("AssetMissing", $"{job}: '{race.Option}' references a missing asset: {relative}.",
                            CatalogIssueSeverity.Error, job, full));
                    }
                    else files[GamePath(file.Name)] = full;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    available = false;
                    issues.Add(new("AssetPathInvalid", $"{job}: invalid asset path in '{race.Option}'.", CatalogIssueSeverity.Error, job));
                }
            }
        }
        if (Property(option, "FileSwaps", out var swaps) && swaps.ValueKind == JsonValueKind.Object)
            foreach (var swap in swaps.EnumerateObject())
                if (swap.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(swap.Value.GetString()))
                    files[GamePath(swap.Name)] = GamePath(swap.Value.GetString()!);
        var effect = GamePath(race.SourceEffectGamePath);
        if (files.TryGetValue(effect, out var sourceEffect))
        {
            // Resolve any local swap chain before aliasing the selected aura to the Emperor carrier.
            var visited = new HashSet<string>(Paths) { effect };
            while (!Path.IsPathRooted(sourceEffect) && files.TryGetValue(sourceEffect, out var next) && visited.Add(sourceEffect))
                sourceEffect = next;
            if (!Path.IsPathRooted(sourceEffect) && visited.Contains(sourceEffect))
            {
                available = false;
                issues.Add(new("EffectSwapCycle", $"{job}: effect swap cycle in '{race.Option}'.", CatalogIssueSeverity.Error, job));
            }
            files[GamePath(emperor)] = sourceEffect;
        }
        else
        {
            available = false;
            issues.Add(new("EffectMissing", $"{job}: '{race.Option}' does not supply {effect}.", CatalogIssueSeverity.Error, job));
        }
        return (files, available);
    }

    private static CandidatePathSnapshot GetCandidates(LoadLocation? current, Guid collectionId, CancellationToken ct)
    {
        var issues = new List<CatalogIssue>();
        var result = new HashSet<string>(Paths);
        if (current is null || string.IsNullOrEmpty(current.ModRoot))
        {
            issues.Add(new("CatalogNotLoaded", "Load the mod catalog before reading collection paths.", CatalogIssueSeverity.Error));
            return new(result, issues, false);
        }
        var collectionRoot = Path.Combine(current.PluginConfigsDir, "Penumbra", "collections");
        var files = EnumerateFiles(collectionRoot, "*.json", issues);
        var effective = new Dictionary<string, bool>(Paths);
        var active = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        Visit(collectionId);
        foreach (var setting in effective.Where(s => s.Value))
        {
            ct.ThrowIfCancellationRequested();
            string directory;
            try { directory = Path.GetFullPath(Path.Combine(current.ModRoot, setting.Key)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                issues.Add(new("StaleMod", $"Collection references an invalid mod directory: {setting.Key}."));
                continue;
            }
            if (!directory.StartsWith(current.ModRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(directory))
            {
                issues.Add(new("StaleMod", $"Collection references an unavailable mod: {setting.Key}."));
                continue;
            }
            foreach (var file in ModJsonFiles(directory, issues))
            {
                ct.ThrowIfCancellationRequested();
                using var doc = ReadJson(file, issues, "EnabledModUnreadable");
                if (doc is not null) CollectFileKeys(doc.RootElement, result);
            }
        }
        return new(result, issues, !issues.Any(i => i.Severity == CatalogIssueSeverity.Error));

        void Visit(Guid id)
        {
            ct.ThrowIfCancellationRequested();
            if (active.Contains(id))
            {
                issues.Add(new("CollectionCycle", "Collection inheritance contains a cycle.", CatalogIssueSeverity.Error));
                return;
            }
            if (!visited.Add(id)) return;
            active.Add(id);
            var path = files.FirstOrDefault(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f), out var g) && g == id);
            if (path is null)
            {
                issues.Add(new("CollectionMissing", $"Collection {id} was not found.", CatalogIssueSeverity.Error));
                active.Remove(id);
                return;
            }
            using var doc = ReadJson(path, issues, "CollectionUnreadable");
            if (doc is null) { active.Remove(id); return; }
            var root = doc.RootElement;
            // Own settings win; first inherited collection wins over later inherited collections.
            if (Property(root, "Settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
                foreach (var setting in settings.EnumerateObject())
                {
                    if (setting.Value.ValueKind == JsonValueKind.Null) continue; // inherited setting
                    if (Property(setting.Value, "Enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        effective.TryAdd(setting.Name, enabled.GetBoolean());
                }
            foreach (var inherited in Array(root, "Inheritance"))
            {
                var text = inherited.ValueKind == JsonValueKind.String ? inherited.GetString()
                    : Text(inherited, "Id");
                if (Guid.TryParse(text, out var parent)) Visit(parent);
                else issues.Add(new("InheritanceInvalid", "Collection has an invalid inherited collection ID.", CatalogIssueSeverity.Error));
            }
            active.Remove(id);
        }
    }

    private RecipeDocument ReadRecipes(List<CatalogIssue> issues)
    {
        try
        {
            using var stream = recipesPath is not null ? File.OpenRead(recipesPath) : OpenBundledRecipes();
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var recipes = JsonSerializer.Deserialize<RecipeDocument>(reader.ReadToEnd(), SerializerOptions)
                ?? throw new InvalidDataException("Recipe document is empty.");
            if (recipes.Jobs.Length == 0 || recipes.Jobs.Any(j => j.Races.Length == 0 || j.JobId == 0))
                throw new InvalidDataException("Recipe jobs are missing.");
            return recipes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or ArgumentException or NotSupportedException)
        {
            issues.Add(new("RecipesUnreadable", $"Bundled matching recipes could not be read: {ex.Message}", CatalogIssueSeverity.Error));
            return new();
        }
    }

    private static Stream OpenBundledRecipes()
    {
        var assembly = typeof(ModCatalog).Assembly;
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".recipes.json", StringComparison.OrdinalIgnoreCase));
        if (resource is not null)
            return assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException("The embedded matching recipes could not be opened. Reinstall the plugin package.");

        // Dalamud can load assemblies from bytes, in which case Location is empty.
        // Such assemblies must contain the recipes; process/current directories do
        // not identify the plugin installation and must not be used as a fallback.
        var directory = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrWhiteSpace(directory))
            throw new FileNotFoundException("Matching recipes are not embedded and the plugin assembly has no file location. Reinstall the current plugin package.");
        return File.OpenRead(Path.Combine(directory, "assets", "recipes.json"));
    }

    private static void AddMissingJobs(RecipeDocument recipes, Dictionary<uint, JobCatalog> jobs, List<CatalogIssue> issues)
    {
        foreach (var recipe in recipes.Jobs)
            if (jobs[recipe.JobId].SourceDirectory is null)
                issues.Add(new("JobMissing", $"{recipe.JobTag}: no installed source mod with a matching meta.json name was found.",
                    CatalogIssueSeverity.Warning, recipe.JobTag));
    }

    private static int CountMatchingOptions(JsonElement? group, JobRecipe recipe)
        => group is null ? 0 : recipe.Races.Select(r => r.Option).Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(name => Array(group.Value, "Options").Any(o => NameEquals(Text(o, "Name"), name)));

    private static void CollectHeadCarriers(JsonElement element, HashSet<string> whitelist, HashSet<string> carriers)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (Text(element, "Type").Equals("Imc", StringComparison.OrdinalIgnoreCase)
                && Property(element, "Manipulation", out var m)
                && Text(m, "ObjectType").Equals("Equipment", StringComparison.OrdinalIgnoreCase)
                && Text(m, "EquipSlot").Equals("Head", StringComparison.OrdinalIgnoreCase)
                && Property(m, "Entry", out var entry) && Number(entry, "VfxId") > 0)
            {
                var primary = Number(m, "PrimaryId");
                var vfx = Number(entry, "VfxId");
                whitelist.Add($"Equipment:{primary}:{Number(m, "SecondaryId")}:{Number(m, "Variant")}:Head:{Text(m, "BodySlot", "Unknown")}");
                carriers.Add($"chara/equipment/e{primary:D4}/vfx/eff/ve{vfx:D4}.avfx");
            }
            foreach (var property in element.EnumerateObject()) CollectHeadCarriers(property.Value, whitelist, carriers);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CollectHeadCarriers(item, whitelist, carriers);
    }

    private static void CollectFileKeys(JsonElement element, HashSet<string> keys)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if ((property.Name.Equals("Files", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("FileSwaps", StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind == JsonValueKind.Object)
                    foreach (var file in property.Value.EnumerateObject()) keys.Add(GamePath(file.Name));
                else CollectFileKeys(property.Value, keys);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CollectFileKeys(item, keys);
    }

    private static IEnumerable<string> ModJsonFiles(string directory, List<CatalogIssue> issues)
    {
        var files = EnumerateFiles(directory, "group_*.json", issues).ToList();
        var defaultMod = Path.Combine(directory, "default_mod.json");
        if (File.Exists(defaultMod)) files.Insert(0, defaultMod);
        return files;
    }

    private static string[] EnumerateFiles(string directory, string pattern, List<CatalogIssue> issues)
    {
        try { return Directory.GetFiles(directory, pattern).OrderBy(p => p, Paths).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new("DirectoryUnreadable", $"A directory could not be read: {directory}.", CatalogIssueSeverity.Error, Path: directory));
            return System.Array.Empty<string>();
        }
    }

    private static string[] EnumerateDirectories(string directory, List<CatalogIssue> issues)
    {
        try { return Directory.GetDirectories(directory).OrderBy(p => p, Paths).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new("DirectoryUnreadable", $"The mod directory could not be read: {directory}.", CatalogIssueSeverity.Error, Path: directory));
            return System.Array.Empty<string>();
        }
    }

    private static JsonDocument? ReadJson(string path, List<CatalogIssue> issues, string code, string? job = null)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Existing Penumbra files may have an UTF-8 BOM, which JsonDocument.Parse(stream) rejects.
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return JsonDocument.Parse(reader.ReadToEnd(), DocumentOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            issues.Add(new(code, $"Could not read {Path.GetFileName(path)}: {ex.Message}", CatalogIssueSeverity.Error, job, path));
            return null;
        }
    }

    private static string GamePath(string value) => value.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    private static bool NameEquals(string actual, string recipe)
        => actual.Split('/')[0].Trim().Equals(recipe.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool Property(JsonElement value, string name, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { result = property.Value; return true; }
        result = default;
        return false;
    }
    private static string Text(JsonElement value, string name, string fallback = "")
        => Property(value, name, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() ?? fallback : fallback;
    private static uint Number(JsonElement value, string name)
        => Property(value, name, out var result) && result.ValueKind == JsonValueKind.Number && result.TryGetUInt32(out var number) ? number : 0;
    private static IEnumerable<JsonElement> Array(JsonElement value, string name)
        => Property(value, name, out var result) && result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private sealed record LoadLocation(string PluginConfigsDir, string ModRoot);
    private sealed record ModDocument(string File, JsonElement Root);
    private sealed record InstalledMod(string Directory, string JobTag, bool Swapped, List<ModDocument> Documents);
    private sealed class RecipeDocument
    {
        public string EmperorEffectPath { get; set; } = "chara/equipment/e0279/vfx/eff/ve0001.avfx";
        public JobRecipe[] Jobs { get; set; } = [];
    }
    private sealed class JobRecipe
    {
        public string JobTag { get; set; } = string.Empty;
        public uint JobId { get; set; }
        public string Group { get; set; } = string.Empty;
        public RaceRecipe[] Races { get; set; } = [];
    }
    private sealed class RaceRecipe
    {
        public string RaceSex { get; set; } = string.Empty;
        public string Option { get; set; } = string.Empty;
        public string SourceEffectGamePath { get; set; } = string.Empty;
    }
}
