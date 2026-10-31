[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $project 'obj\engine-checks'
& (Join-Path $project 'build.ps1') -Compiler Roslyn -OutputDirectory $testRoot
$product = Join-Path $testRoot 'PrimalArmor.dll'
$dalamud = Join-Path $env:APPDATA 'FFXIVSimpleLauncher\Dalamud\Injector\Dalamud.dll'
[Reflection.Assembly]::LoadFrom($dalamud) | Out-Null
[Reflection.Assembly]::LoadFrom($product) | Out-Null
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
$runtimeRoot = Join-Path $env:APPDATA 'FFXIVSimpleLauncher\Dalamud\Runtime\shared\Microsoft.NETCore.App'
$runtime = Get-ChildItem -LiteralPath $runtimeRoot -Directory | Where-Object Name -Match '^9\.' |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in @((Get-ChildItem -LiteralPath $runtime.FullName -Filter '*.dll').FullName) + @($product,$dalamud)) {
    try { $null = [Reflection.AssemblyName]::GetAssemblyName($file)
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file))
    } catch [BadImageFormatException] { }
}
$source = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PrimalArmor;
using PrimalArmor.Models;
using PrimalArmor.Interop;
using PrimalArmor.Runtime;

public static class EngineChecks
{
    private static int assertions;
    private static readonly List<string> failures = new();
    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Scenario(string name, Action run)
    {
        try { run(); }
        catch (Exception e) { failures.Add(name + ": " + e.Message); }
    }
    public static string[] Run()
    {
        Scenario("different jobs and shared race options", () =>
        {
            using var f = new Fixture();
            f.Scene(P(2,20),P(4,25),P(6,20,"HighlanderMale"));
            f.Pump();
            Assert(f.Engine.ActivePlayers==3,"Three compatible players should retain auras simultaneously.");
            var monk=f.Ipc.Assigned[2]; var mage=f.Ipc.Assigned[4]; var monkRace=f.Ipc.Assigned[6];
            Assert(monk!=mage,"Different jobs must not share an aura collection.");
            Assert(monk==monkRace,"Same job/option across races should share a collection.");
            Assert(f.Ipc.Bundles[monk].Aura!["vfx/test/job.avfx"]=="MNK.avfx","MNK aura was overwritten.");
            Assert(f.Ipc.Bundles[mage].Aura!["vfx/test/job.avfx"]=="BLM.avfx","BLM aura was overwritten.");
            Assert(f.Ipc.Bundles[monk].BaseMeta=="clean metadata","Base metadata was not neutralized.");
            Assert(f.Ipc.Bundles[monk].BaseFiles.ContainsKey("vfx/test/shared.atex"),"Unrelated base resources were lost.");
            Assert(!f.Ipc.Bundles[monk].BaseFiles.ContainsKey("chara/equipment/e0188/vfx/eff/ve0001.avfx"),"Original carrier survived base copy.");
            var assignments=f.Ipc.AssignmentCount;
            f.Pump();
            Assert(f.Ipc.AssignmentCount==assignments,"Stable players were reassigned repeatedly.");
        });
        Scenario("non-Primal first sight neutralization", () =>
        {
            using var f=new Fixture(); f.Scene(P(2,19)); f.Pump();
            Assert(f.Ipc.Assigned.ContainsKey(2),"Unsupported first-seen player did not receive neutral base.");
            var bundle=f.Ipc.Bundles[f.Ipc.Assigned[2]];
            Assert(bundle.Aura is null && bundle.BaseMeta=="clean metadata","Unsupported player retained a source aura.");
            Assert(f.Ipc.HeadWrites.Count==0 && f.Ipc.HatWrites==0,"Neutralization should preserve the player's native head.");
            Assert(f.Engine.ActivePlayers==0,"Neutral isolation counted as an active aura.");
        });
        Scenario("unsupported job transition restores native head", () =>
        {
            using var f=new Fixture(); f.Scene(P(2,20)); f.Pump();
            f.Scene(P(2,19)); f.Pump();
            var head=f.Ipc.ReadHead(2);
            Assert(head.ItemId==502 && head.Stains.SequenceEqual(new byte[]{7,8}) && !head.HatVisible,"Original head/stains/visibility were not restored.");
            Assert(f.Ipc.Bundles[f.Ipc.Assigned[2]].Aura is null,"Unsupported job retained its prior aura.");
        });
        Scenario("range party and player cap", () =>
        {
            using var f=new Fixture();
            f.Config.PartyOnly=true; f.Config.MaxDistance=15; f.Config.MaxPlayers=1;
            f.Scene(P(2,20,distance:3,party:false),P(4,20,distance:5),P(6,25,distance:7),P(8,20,distance:50));
            f.Pump();
            Assert(f.HasAura(4) && !f.HasAura(2) && !f.HasAura(6) && !f.HasAura(8),"Party/range/max target policy selected wrong players.");
            Assert(f.Ipc.ReadHead(2).ItemId==502 && f.Ipc.ReadHead(6).ItemId==506 && f.Ipc.ReadHead(8).ItemId==508,"Excluded players' heads changed.");
            f.Config.PartyOnly=false; f.Pump();
            Assert(f.HasAura(2) && !f.HasAura(4),"Changed target policy did not move aura to the nearest eligible player.");
            Assert(f.Ipc.ReadHead(4).ItemId==504,"Player excluded by the new cap kept Emperor's hat.");
            f.Config.MaxDistance=1; f.Pump();
            Assert(f.Engine.ActivePlayers==0 && f.Ipc.ReadHead(2).ItemId==502,"Leaving range did not restore the player.");
        });
        Scenario("missing compatible variant does not consume cap", () =>
        {
            using var f=new Fixture(); f.Config.MaxPlayers=1;
            f.Scene(P(2,22,distance:1),P(4,20,distance:2)); f.Pump();
            Assert(!f.HasAura(2) && f.HasAura(4),"Job without an installed compatible variant consumed the aura cap.");
        });
        Scenario("queued job change is refreshed", () =>
        {
            using var f=new Fixture(); f.Scene(P(2,20),P(4,20),P(6,25));
            f.Step(0); f.Step(1000);
            Assert(f.Engine.PendingPlayers==2,"Fixture did not preserve queued targets.");
            f.Scene(P(2,20),P(4,25),P(6,19)); f.Step(1200); f.Step(1400);
            Assert(f.Ipc.Bundles[f.Ipc.Assigned[4]].Aura!["vfx/test/job.avfx"]=="BLM.avfx","Queued actor received stale MNK aura after changing job.");
            Assert(!f.HasAura(6) && f.Ipc.ReadHead(6).ItemId==506,"Queued unsupported actor received its stale BLM aura.");
        });
        Scenario("index reuse does not restore another player", () =>
        {
            using var f=new Fixture(); f.Scene(P(2,20)); f.Pump();
            f.Scene(P(2,19,objectId:222,identity:"replacement"));
            f.Engine.Reset();
            Assert(f.Ipc.ReadHead(2).ItemId==502,"Cleanup overwrote the replacement player's native head.");
            Assert(f.Ipc.HeadWrites.Count(w=>w.ItemId==502)==0,"Old state restored a head to a reused actor index.");
            Assert(f.Engine.TemporaryCollections==0 && f.Ipc.Bundles.Count==0,"Reset leaked temporary collections.");
        });
        Scenario("same identity reappears with a new native head", () =>
        {
            using var f=new Fixture(); f.Scene(P(2,20)); f.Pump();
            f.Scene(P(2,20,objectId:222)); f.Ipc.Heads[2]=new(777,new byte[]{3,4},true);
            f.Pump(); f.Engine.Reset();
            Assert(f.Ipc.ReadHead(2).ItemId==777 && f.Ipc.ReadHead(2).Stains.SequenceEqual(new byte[]{3,4}),"Reappearing player's old saved head overwrote their new native head.");
        });
        Scenario("external head change is preserved during cleanup", () =>
        {
            using var f=new Fixture(); f.Scene(P(2,20)); f.Pump();
            f.Ipc.Heads[2]=new(888,new byte[]{10,11},true);
            f.Engine.Reset();
            Assert(f.Ipc.ReadHead(2).ItemId==888 && f.Ipc.ReadHead(2).Stains.SequenceEqual(new byte[]{10,11}),"Cleanup overwrote a head changed by another tool.");
        });
        Scenario("logout and reset remove all temporary collections", () =>
        {
            using var f=new Fixture(); f.Scene(P(2,20),P(4,25),P(6,19)); f.Pump();
            Assert(f.Engine.TemporaryCollections==3,"Fixture should have two aura bundles and one neutral bundle.");
            f.Current=Scene.Empty; f.Step(f.Now+200);
            Assert(f.Engine.TemporaryCollections==0 && f.Ipc.Bundles.Count==0 && f.Engine.PendingPlayers==0,"Logout leaked collections or queued players.");
            f.Scene(P(2,20)); f.Pump(); f.Engine.Reset();
            Assert(f.Engine.TemporaryCollections==0 && f.Ipc.Bundles.Count==0,"Explicit reset leaked collections.");
        });
        Scenario("logout clears pending snapshot work", () =>
        {
            var delayed=new TaskCompletionSource<CandidatePathSnapshot>();
            using var f=new Fixture(delayed.Task); f.Scene(P(2,20)); f.Step(0);
            Assert(f.CandidateRequests==1,"Fixture did not start pending discovery.");
            f.Current=Scene.Empty; f.Step(200);
            f.Scene(P(2,20,objectId:222)); f.Step(1400);
            Assert(f.CandidateRequests==2,"Logout retained the prior session's base snapshot.");
        });
        Scenario("failed collection deletion stays owned until retry", () =>
        {
            using var f=new Fixture(); f.Scene(P(2,20)); f.Pump();
            f.Ipc.DeleteFailures=1; f.Engine.Reset();
            Assert(f.Engine.HasCleanupPending && f.Engine.TemporaryCollections==1 && f.Ipc.Bundles.Count==1,"Failed deletion lost ownership of a temporary collection.");
            f.Engine.Reset();
            Assert(!f.Engine.HasCleanupPending && f.Engine.TemporaryCollections==0 && f.Ipc.Bundles.Count==0,"Second cleanup did not remove the retained temporary collection.");
        });
        Scenario("snapshot neutralization failure is not repeated every tick", () =>
        {
            var neutralizeCalls=0;
            using var f=new Fixture(neutralizer:(meta,keys)=>{neutralizeCalls++;throw new InvalidOperationException("synthetic metadata failure");});
            f.Scene(P(2,20)); f.Pump();
            Assert(neutralizeCalls==1,"Failed snapshot was decoded again on later ticks.");
            Assert(f.Reports.Count is >0 and <=2,"Snapshot failure was reported repeatedly each tick.");
            Assert(f.Engine.TemporaryCollections==0 && f.Ipc.AssignmentCount==0,"Failed snapshot created or assigned a partial collection.");
        });
        return new[]{"ASSERTIONS="+assertions,"FAILURES="+failures.Count}.Concat(failures).ToArray();
    }

    private static TargetInfo P(int index,uint job,string race="MidlanderMale",float? distance=null,bool party=true,ulong? objectId=null,string? identity=null)
        =>new(index,objectId??(ulong)index,identity??("player"+index),job,race,distance??index,party);

    private sealed class Fixture : IDisposable
    {
        public readonly Mock Ipc=new();
        public readonly ArmorEngine Engine;
        public readonly Configuration Config=new(){Enabled=true};
        public Scene Current=PrimalArmor.Runtime.Scene.Empty;
        public long Now=-200;
        public int CandidateRequests;
        public readonly List<string> Reports=new();
        public Fixture(Task<CandidatePathSnapshot>? pending=null,Func<string,IReadOnlySet<string>,string>? neutralizer=null)
        {
            var jobs=new Dictionary<uint,JobCatalog>();
            foreach(var (id,tag) in new[]{(20u,"MNK"),(25u,"BLM")})
                jobs[id]=new(){JobId=id,JobTag=tag,SourceDirectory="synthetic",Variants=new Dictionary<string,ModVariant>
                {
                    ["MidlanderMale"]=Variant(id,tag,"MidlanderMale"),
                    ["HighlanderMale"]=Variant(id,tag,"HighlanderMale"),
                }};
            Engine=new(Ipc,new CatalogSnapshot
            {
                Jobs=jobs,
                WhitelistImcKeys=new HashSet<string>{"Equipment:188:0:1:Head:Unknown"},
                CarrierPaths=new HashSet<string>{"chara/equipment/e0188/vfx/eff/ve0001.avfx"},
            },(id,ct)=>
            {
                CandidateRequests++;
                return pending??Task.FromResult(new CandidatePathSnapshot(new HashSet<string>
                    {"vfx/test/shared.atex","chara/equipment/e0188/vfx/eff/ve0001.avfx"},Array.Empty<CatalogIssue>(),true));
            },neutralizer??((meta,keys)=>"clean metadata"),expected=>Current.Find(expected),Reports.Add);
        }
        private static ModVariant Variant(uint id,string tag,string race)=>new()
        {
            JobId=id,JobTag=tag,RaceSex=race,Option="Shared Option",SourceDirectory="synthetic",
            Files=new Dictionary<string,string>{{"vfx/test/job.avfx",tag+".avfx"}},
        };
        public void Scene(params TargetInfo[] targets)
        {
            Current=new(true,42,targets);
            foreach(var target in targets)
                if(!Ipc.Actors.TryGetValue(target.Index,out var old)||old.ObjectId!=target.ObjectId||old.Identity!=target.Identity)
                {
                    Ipc.Actors[target.Index]=target;
                    Ipc.Heads[target.Index]=new((ulong)(500+target.Index),new byte[]{7,8},false);
                    Ipc.Assigned.Remove(target.Index);
                }
        }
        public void Step(long now)
        {
            Now=now; var before=Ipc.AssignmentCount; Engine.Tick(Current,Config,now);
            Assert(Ipc.AssignmentCount-before<=1,"One tick assigned more than one player.");
        }
        public void Pump(){for(var i=0;i<18;i++)Step(Now+200);}
        public bool HasAura(int index)=>Ipc.Assigned.TryGetValue(index,out var id)&&Ipc.Bundles[id].Aura is not null;
        public void Dispose()=>Engine.Dispose();
    }
    private sealed class Bundle
    {
        public Dictionary<string,string> BaseFiles=new();
        public string BaseMeta="";
        public Dictionary<string,string>? Aura;
    }
    private sealed class Mock : IArmorInterop
    {
        public readonly Dictionary<int,TargetInfo> Actors=new();
        public readonly Dictionary<int,HeadState> Heads=new();
        public readonly Dictionary<int,Guid> Assigned=new();
        public readonly Dictionary<Guid,Bundle> Bundles=new();
        public readonly List<(int Index,ulong ItemId)> HeadWrites=new();
        public int AssignmentCount,HatWrites,DeleteFailures;
        private readonly Guid original=Guid.NewGuid();
        public bool TryConnect(out string error){error="";return true;}
        public CollectionInfo GetCollection(int index)=>new(true,false,original,"Original Collection");
        public string ResolvePath(string path,int index)=>"synthetic/"+path;
        public string GetMeta(int index)=>"primal metadata";
        public Guid CreateCollection(string name){var id=Guid.NewGuid();Bundles.Add(id,new());return id;}
        public void DeleteCollection(Guid id)
        {
            if(DeleteFailures>0){DeleteFailures--;throw new InvalidOperationException("synthetic deletion failure");}
            Bundles.Remove(id);foreach(var index in Assigned.Where(p=>p.Value==id).Select(p=>p.Key).ToArray())Assigned.Remove(index);
        }
        public void AssignCollection(Guid id,int index){Assigned[index]=id;AssignmentCount++;}
        public void AddMod(string tag,Guid id,Dictionary<string,string> files,string meta,int priority)
        {
            if(tag=="PrimalArmor.Base"){Bundles[id].BaseFiles=new(files);Bundles[id].BaseMeta=meta;}
            else Bundles[id].Aura=new(files);
        }
        public void Redraw(int index){}
        public HeadState ReadHead(int index)=>Heads[index];
        public void SetHead(int index,ulong itemId,byte[] stains){Heads[index]=new(itemId,stains.ToArray(),Heads[index].HatVisible);HeadWrites.Add((index,itemId));}
        public void SetHatVisible(int index,bool visible){Heads[index]=Heads[index] with {HatVisible=visible};HatWrites++;}
    }
}
'@
$parse = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp13)
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source,$parse,'EngineChecks.cs')
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$trees.Add($tree)
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).
    WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('PrimalArmor.EngineChecks',$trees,$references,$options)
$stream = [IO.MemoryStream]::new()
try {
    $compiled=$compilation.Emit($stream)
    if(!$compiled.Success){throw ($compiled.Diagnostics -join [Environment]::NewLine)}
    [Reflection.Assembly]::Load($stream.ToArray()) | Out-Null
} finally {$stream.Dispose()}
$results=[EngineChecks]::Run()
$results | ForEach-Object {Write-Host $_}
if($results[1] -ne 'FAILURES=0'){throw 'Engine regression checks failed.'}
