using System.Collections.Generic;
using Dalamud.Configuration;

namespace PrimalArmor;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool StartOnLogin { get; set; } = true;
    public bool PartyOnly { get; set; }
    public float MaxDistance { get; set; } = 40;
    public int MaxPlayers { get; set; } = 40;
    public int ProcessIntervalMs { get; set; } = 150;
    public HashSet<uint> EnabledJobs { get; set; } = new() {20,22,25,33,35,39,40,41};
}
