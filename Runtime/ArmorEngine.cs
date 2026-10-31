using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PrimalArmor.Catalog;
using PrimalArmor.Interop;
using PrimalArmor.Models;

namespace PrimalArmor.Runtime;

/// <summary>Framework-thread state machine; disk discovery runs in the catalog tasks.</summary>
public sealed class ArmorEngine : IDisposable
{
    private readonly IArmorInterop ipc;
    private readonly CatalogSnapshot catalog;
    private readonly Func<Guid, CancellationToken, Task<CandidatePathSnapshot>> getCandidates;
    private readonly Func<string, IReadOnlySet<string>, string> neutralize;
    private readonly Func<TargetInfo, TargetInfo?> findCurrent;
    private readonly Action<string> report;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<Guid, BaseState> bases = new();
    private readonly Dictionary<string, ActorState> actors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> bundles = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> temporary = new();
    private readonly Dictionary<string, long> retryAfter = new(StringComparer.Ordinal);
    private Queue<Work> queue = new();
    private long nextScan, nextMutation;
    private int serial;
    private ulong characterId;
    private bool disposed;
    public string Status { get; private set; } = "待命";
    public int ActivePlayers => actors.Values.Count(s => s.Effect != "none");
    public int TemporaryCollections => temporary.Count;
    public int PendingPlayers => queue.Count;
    public int FullRedraws { get; private set; }
    public int Mutations { get; private set; }
    public bool HasCleanupPending { get; private set; }

    public ArmorEngine(IArmorInterop ipc, CatalogSnapshot catalog,
        Func<Guid, CancellationToken, Task<CandidatePathSnapshot>> getCandidates,
        Func<string,IReadOnlySet<string>,string> neutralize,
        Func<TargetInfo,TargetInfo?> findCurrent, Action<string> report)
    {
        this.ipc=ipc; this.catalog=catalog; this.getCandidates=getCandidates;
        this.neutralize=neutralize; this.findCurrent=findCurrent; this.report=report;
    }

    public void Tick(Scene scene, Configuration config, long now)
    {
        if (disposed) return;
        if (!scene.LoggedIn)
        {
            if (actors.Count > 0 || temporary.Count > 0 || bases.Count > 0 || queue.Count > 0) Reset();
            characterId=0; Status="等待登入"; return;
        }
        if (characterId != 0 && characterId != scene.CharacterId) Reset();
        characterId=scene.CharacterId;
        // The lookup closure for each base can choose a currently visible actor
        // before it is assigned any of our own temporary collections.
        AdvanceSnapshots(scene);
        if (now >= nextScan && queue.Count == 0)
        {
            nextScan=now+1000;
            var selected=TargetPolicy.SelectAuraTargets(scene,config,HasVariant);
            queue=new Queue<Work>(scene.Players.OrderBy(p=>p.Distance).Select(p=>
            {
                var variant=selected.Contains(p.Identity) ? catalog.FindVariant(p.JobId,p.RaceSex) : null;
                return new Work(p,variant is { Available:true } ? variant : null);
            }).Where(w=>NeedsWork(w) && (!retryAfter.TryGetValue(w.Target.Identity,out var until)||until<=now)));
        }
        if (now < nextMutation) return;
        // At most one new assignment per interval. Unchanged actors do not enter
        // this queue, and candidate resolution is separately frame-budgeted.
        while (queue.TryDequeue(out var work))
        {
            var current=findCurrent(work.Target);
            if (current is null) continue;
            try
            {
                var eligible=TargetPolicy.SelectAuraTargets(scene,config,HasVariant).Contains(current.Identity);
                var variant=eligible ? catalog.FindVariant(current.JobId,current.RaceSex) : null;
                var fresh=new Work(current,variant is { Available:true } ? variant : null);
                if (!NeedsWork(fresh) || !TryApply(fresh)) continue;
                nextMutation=now+Math.Clamp(config.ProcessIntervalMs,50,2000);
                Mutations++; Status="效果已套用"; break;
            }
            catch(Exception ex)
            {
                retryAfter[work.Target.Identity]=now+30000;
                Status=ex.Message;
                report(ex.Message);
                nextMutation=now+Math.Clamp(config.ProcessIntervalMs,50,2000);
                break;
            }
        }
    }

    private bool NeedsWork(Work work)
    {
        var effect=work.Variant?.CacheKey ?? "none";
        return !actors.TryGetValue(work.Target.Identity,out var state)
            || state.Effect!=effect || state.Target.ObjectId!=work.Target.ObjectId
            || state.Target.Index!=work.Target.Index;
    }
    private bool HasVariant(TargetInfo target) => catalog.FindVariant(target.JobId,target.RaceSex) is {Available:true};

    private bool TryApply(Work work)
    {
        var target=work.Target;
        actors.TryGetValue(target.Identity,out var state);
        if(state is not null && (state.Target.ObjectId!=target.ObjectId || state.Target.Index!=target.Index)
            && state.Head is not null && ipc.ReadHead(target.Index).ItemId!=10032)
            state.Head=null; // A re-created actor has a fresh native head; do not restore an old one.
        if (state is null)
        {
            var collection=ipc.GetCollection(target.Index);
            if (!collection.Valid) throw new InvalidOperationException("無法辨識玩家集合");
            if (collection.Name.StartsWith("PrimalArmor-",StringComparison.Ordinal))
                throw new InvalidOperationException("偵測到舊版 SND 特效，請先停止 Primal Armor Auto 巨集。");
            if (!bases.TryGetValue(collection.Id,out var source))
            {
                source=new BaseState(collection.Id,target,getCandidates(collection.Id,cancellation.Token));
                bases.Add(collection.Id,source);
            }
            if (source.Error is not null) throw new InvalidOperationException(source.Error);
            if (!source.Ready) { Status="正在準備原本的技能設定…"; return false; }
            state=new ActorState(target,source);
        }
        var effect=work.Variant?.CacheKey ?? "none";
        if (effect=="none" && !state.Base.RequiresIsolation && state.Head is null)
        {
            state.Target=target; state.Effect="none";
            actors[target.Identity]=state;
            return false;
        }
        var previous=state.Effect;
        var id=GetBundle(state.Base,work.Variant);
        // Register partial work before the first mutating IPC so cleanup owns it.
        state.Target=target;
        actors[target.Identity]=state;
        if (work.Variant is not null && state.Head is null) state.Head=ipc.ReadHead(target.Index);
        ipc.AssignCollection(id,target.Index);
        if (work.Variant is not null)
        {
            var before=ipc.ReadHead(target.Index);
            ipc.SetHead(target.Index,10032,new byte[]{0,0});
            if (!before.HatVisible) ipc.SetHatVisible(target.Index,true);
            if (before.ItemId==10032 || (previous!="none" && previous!="pending")) Redraw(target.Index);
        }
        else
        {
            RestoreHead(state);
            state.Head=null;
            Redraw(target.Index);
        }
        state.Effect=effect;
        return true;
    }

    private Guid GetBundle(BaseState source, ModVariant? variant)
    {
        var key=source.Id+":"+(variant?.CacheKey??"none");
        if (bundles.TryGetValue(key,out var existing)) return existing;
        var id=ipc.CreateCollection("PrimalArmorPlugin-"+(++serial));
        temporary.Add(id);
        ipc.AddMod("PrimalArmor.Base",id,source.Files,source.Metadata,0);
        if (variant is not null)
            ipc.AddMod("PrimalArmor.Aura",id,new Dictionary<string,string>(variant.Files),MetaNeutralizer.EmperorHatMetadata,1000000);
        bundles.Add(key,id);
        return id;
    }

    private void AdvanceSnapshots(Scene scene)
    {
        var timer=Stopwatch.StartNew();
        var calls=0;
        foreach(var source in bases.Values.Where(b=>!b.Ready && b.Error is null))
        {
          try
          {
            if (!source.Candidates.IsCompleted) continue;
            if (source.Candidates.IsCanceled) { source.Error="讀取設定已取消"; continue; }
            if (source.Candidates.IsFaulted) { source.Error="無法讀取此玩家的原始集合"; continue; }
            var paths=source.Candidates.Result;
            if (!paths.Complete)
            {
                source.Error="玩家使用無法完整讀取的集合，已略過（同步插件／其他暫存集合）。";
                continue;
            }
            var actor=scene.Players.FirstOrDefault(p=>p.Identity==source.Source.Identity)
                ?? scene.Players.FirstOrDefault(p=>!actors.ContainsKey(p.Identity)
                    && ipc.GetCollection(p.Index).Id==source.Id);
            if(actor is not null)actor=findCurrent(actor);
            if(actor is null) continue;
            source.Source=actor;
            if (source.Remaining is null)
            {
                var original=ipc.GetMeta(actor.Index);
                source.Metadata=neutralize(original,catalog.WhitelistImcKeys);
                source.RequiresIsolation=source.Metadata!=original;
                source.Remaining=new Queue<string>(paths.Paths);
            }
            while (source.Remaining.TryDequeue(out var path))
            {
                var resolved=ipc.ResolvePath(path,actor.Index);
                if (!string.IsNullOrEmpty(resolved) && !resolved.Equals(path,StringComparison.OrdinalIgnoreCase))
                {
                    if(catalog.CarrierPaths.Contains(path.ToLowerInvariant())) source.RequiresIsolation=true;
                    else source.Files[path]=resolved;
                }
                calls++;
                if(calls>=32 || timer.Elapsed.TotalMilliseconds>=2) return;
            }
            source.Ready=true;
          }
          catch(Exception ex)
          {
              source.Error="準備技能設定失敗："+ex.Message;
              Status=source.Error;
              report(source.Error);
          }
        }
    }

    private void RestoreHead(ActorState state)
    {
        if(state.Head is not { } head || findCurrent(state.Target) is null) return;
        // Do not overwrite a head changed by another appearance tool meanwhile.
        if(ipc.ReadHead(state.Target.Index).ItemId!=10032) return;
        ipc.SetHead(state.Target.Index,head.ItemId,head.Stains);
        ipc.SetHatVisible(state.Target.Index,head.HatVisible);
    }
    private void Redraw(int index) { ipc.Redraw(index); FullRedraws++; }

    public void Reset()
    {
        var headFailures=0;
        foreach(var state in actors.Values)
            try { RestoreHead(state); } catch(Exception ex) { headFailures++; report("頭部還原失敗："+ex.Message); }
        foreach(var id in temporary.ToArray())
        {
            try { ipc.DeleteCollection(id); temporary.Remove(id); }
            catch(Exception ex) { report("暫存特效清除失敗（"+id+"）："+ex.Message); }
        }
        foreach(var state in actors.Values)
            if(findCurrent(state.Target) is not null)
                try { Redraw(state.Target.Index); } catch(Exception ex) { report(ex.Message); }
        HasCleanupPending=temporary.Count>0 || headFailures>0;
        if(!HasCleanupPending){actors.Clear(); bases.Clear(); bundles.Clear(); retryAfter.Clear();}
        queue.Clear();
        Status=!HasCleanupPending ? "已還原並清除暫存設定" : "部分清理失敗，正在保留紀錄以便重試";
    }

    public void Dispose()
    {
        if(disposed)return;
        cancellation.Cancel(); Reset(); cancellation.Dispose(); disposed=true;
    }
    private sealed record Work(TargetInfo Target,ModVariant? Variant);
    private sealed class ActorState(TargetInfo target,BaseState source)
    {
        public TargetInfo Target=target;
        public BaseState Base=source;
        public HeadState? Head;
        public string Effect="pending";
    }
    private sealed class BaseState(Guid id,TargetInfo source,Task<CandidatePathSnapshot> candidates)
    {
        public Guid Id=id;
        public TargetInfo Source=source;
        public Task<CandidatePathSnapshot> Candidates=candidates;
        public Queue<string>? Remaining;
        public Dictionary<string,string> Files=new(StringComparer.OrdinalIgnoreCase);
        public string Metadata="";
        public bool RequiresIsolation,Ready;
        public string? Error;
    }
}
