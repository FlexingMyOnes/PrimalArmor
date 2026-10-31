using System;
using System.Collections.Generic;
using System.Linq;

namespace PrimalArmor.Runtime;

public sealed record TargetInfo(int Index, ulong ObjectId, string Identity,
    uint JobId, string RaceSex, float Distance, bool InParty);

public sealed record Scene(bool LoggedIn, ulong CharacterId, IReadOnlyList<TargetInfo> Players)
{
    public static Scene Empty { get; } = new(false, 0, Array.Empty<TargetInfo>());
    public TargetInfo? Find(TargetInfo target) => Players.FirstOrDefault(p =>
        p.Index == target.Index && p.ObjectId == target.ObjectId && p.Identity == target.Identity);
}

public static class TargetPolicy
{
    public static HashSet<string> SelectAuraTargets(Scene scene, Configuration config,
        Func<TargetInfo,bool>? hasVariant = null)
        => scene.Players.Where(p => p.Distance <= Math.Max(1, config.MaxDistance)
                && (!config.PartyOnly || p.InParty) && config.EnabledJobs.Contains(p.JobId)
                && (hasVariant is null || hasVariant(p)))
            .OrderBy(p => p.Distance).ThenBy(p => p.Identity, StringComparer.Ordinal)
            .Take(Math.Clamp(config.MaxPlayers, 1, 200))
            .Select(p => p.Identity).ToHashSet(StringComparer.Ordinal);
}
