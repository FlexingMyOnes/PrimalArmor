using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PrimalArmor.Catalog;
using PrimalArmor.Interop;
using PrimalArmor.Models;
using PrimalArmor.Runtime;

namespace PrimalArmor;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objects;
    private readonly IPartyList party;
    private readonly ICommandManager commands;
    private readonly IPluginLog log;
    private readonly IpcBridge ipc;
    private readonly MetaNeutralizer metadata = new();
    private readonly object gate = new();
    private readonly Configuration config;
    private readonly string pluginConfigs;
    private CancellationTokenSource scanCancellation = new();
    private ModCatalog? catalogService;
    private Task<CatalogSnapshot>? scanTask;
    private CatalogSnapshot? catalog;
    private ArmorEngine? engine;
    private readonly List<ArmorEngine> pendingCleanup=new();
    private long nextCleanupRetry;
    private bool show=true,requestScan=true,manualStart,lastLoggedIn,sessionAllowed,disposed;
    private long readyAfter;
    private long nextSceneRead;
    private Scene cachedScene=Scene.Empty;
    private string status="準備掃描模組…";
    private string lastError="";
    private Stats stats = new(0,0,0,0,0);
    private readonly Stopwatch clock=Stopwatch.StartNew();

    public Plugin(IDalamudPluginInterface pi,IFramework framework,IClientState client,IPlayerState playerState,
        IObjectTable objects,IPartyList party,ICommandManager commands,IPluginLog log)
    {
        this.pi=pi; this.framework=framework; this.client=client; this.playerState=playerState; this.objects=objects;
        this.party=party; this.commands=commands; this.log=log;
        try { config=pi.GetPluginConfig() as Configuration ?? new Configuration(); }
        catch { config=new Configuration(); }
        config.EnabledJobs ??= new HashSet<uint>{20,22,25,33,35,39,40,41};
        pluginConfigs=Path.GetDirectoryName(pi.GetPluginConfigDirectory())
            ?? throw new InvalidOperationException("無法找到插件設定資料夾");
        ipc=new IpcBridge(pi,framework);
        commands.AddHandler("/primalarmor",new CommandInfo(OnCommand){HelpMessage="開啟 Primal Armor 設定；on 啟用、off 停止並還原"});
        pi.UiBuilder.Draw+=Draw;
        pi.UiBuilder.OpenConfigUi+=Open;
        pi.UiBuilder.OpenMainUi+=Open;
        framework.Update+=Update;
    }

    private void Open() => show=true;
    private void OnCommand(string command,string args)
    {
        lock(gate)
        {
            switch(args.Trim().ToLowerInvariant())
            {
                case "on":config.Enabled=true;manualStart=true;Save();break;
                case "off":config.Enabled=false;Save();break;
                default:show=true;break;
            }
        }
    }
    private void Save() => pi.SavePluginConfig(config);
    private void Report(string message)
    {
        lastError=message;
        log.Warning("Primal Armor: {Message}",message);
    }

    private void StartScan()
    {
        StopEngine();
        scanCancellation.Cancel(); scanCancellation.Dispose(); scanCancellation=new();
        catalogService=new ModCatalog();
        catalog=null;
        lastError="";
        scanTask=catalogService.LoadAsync(pluginConfigs,scanCancellation.Token);
        status="正在背景掃描已安裝模組…";
    }
    private void StopEngine()
    {
        var old=engine;engine=null;
        if(old is null)return;
        old.Dispose();
        if(old.HasCleanupPending)pendingCleanup.Add(old);
    }

    private void Update(IFramework _)
    {
        if(disposed)return;
        try
        {
            lock(gate)
            {
                if(disposed)return;
                if(pendingCleanup.Count>0 && clock.ElapsedMilliseconds>=nextCleanupRetry)
                {
                    nextCleanupRetry=clock.ElapsedMilliseconds+2000;
                    foreach(var retired in pendingCleanup.ToArray())
                    {
                        retired.Reset();
                        if(!retired.HasCleanupPending)pendingCleanup.Remove(retired);
                    }
                }
                if(requestScan){requestScan=false;StartScan();}
                if(scanTask is {IsCompleted:true})
                {
                    if(scanTask.IsCompletedSuccessfully){catalog=scanTask.Result;status="模組偵測完成";}
                    else if(scanTask.IsFaulted){status="掃描失敗";Report(scanTask.Exception?.GetBaseException().Message??status);}
                    scanTask=null;
                }
                var loggedIn=client.IsLoggedIn && objects.LocalPlayer is not null;
                if(loggedIn && !lastLoggedIn){sessionAllowed=config.StartOnLogin;readyAfter=clock.ElapsedMilliseconds+3000;nextSceneRead=0;}
                if(!loggedIn && lastLoggedIn){StopEngine();sessionAllowed=false;}
                lastLoggedIn=loggedIn;
                if(manualStart){manualStart=false;sessionAllowed=true;readyAfter=clock.ElapsedMilliseconds+1000;}
                if(!config.Enabled){StopEngine();status=pendingCleanup.Count>0?"正在重試清除暫存設定":"已停用；原始模組與設計未改寫";UpdateStats();return;}
                if(pendingCleanup.Count>0){status="等待前次暫存設定清理完成";UpdateStats();return;}
                if(!loggedIn){status="等待登入";UpdateStats();return;}
                if(!sessionAllowed){status="等待本次手動啟動";UpdateStats();return;}
                if(catalog is null || catalogService is null || clock.ElapsedMilliseconds<readyAfter){UpdateStats();return;}
                if(!ipc.TryConnect(out var why)){status=why;UpdateStats();return;}
                engine ??= new ArmorEngine(ipc,catalog,catalogService.GetCandidatePathSnapshotAsync,
                    metadata.Clear,FindCurrent,Report);
                if(clock.ElapsedMilliseconds>=nextSceneRead)
                {
                    cachedScene=ReadScene();nextSceneRead=clock.ElapsedMilliseconds+250;
                }
                engine.Tick(cachedScene,config,clock.ElapsedMilliseconds);
                status=engine.Status;
                UpdateStats();
            }
        }
        catch(Exception ex){status="暫停處理，請檢查診斷訊息";Report(ex.Message);}
    }

    private void UpdateStats() => stats=engine is null ? new(0,0,0,0,0) :
        new(engine.ActivePlayers,engine.PendingPlayers,engine.TemporaryCollections,engine.Mutations,engine.FullRedraws);

    private static string Identity(IPlayerCharacter actor)=>actor.Name.TextValue+"@"+actor.HomeWorld.RowId;
    private static string RaceSex(IPlayerCharacter actor)
    {
        var c=actor.Customize;
        if(c.Length<5)return "";
        var race=c[0] switch
        {
            1=>c[4]==2?"Highlander":"Midlander",2=>"Elezen",3=>"Lalafell",4=>"Miqote",
            5=>"Roegadyn",6=>"AuRa",7=>"Hrothgar",8=>"Viera",_=>""
        };
        return race+(c[1]==0?"Male":"Female");
    }
    private TargetInfo? FindCurrent(TargetInfo expected)
    {
        if(expected.Index<0 || expected.Index>=objects.Length)return null;
        if(objects[expected.Index] is not IPlayerCharacter actor || actor.GameObjectId!=expected.ObjectId
            || Identity(actor)!=expected.Identity)return null;
        var self=objects.LocalPlayer;
        if(self is null || actor.GameObjectId==self.GameObjectId)return null;
        return new(actor.ObjectIndex,actor.GameObjectId,Identity(actor),actor.ClassJob.RowId,RaceSex(actor),
            Vector3.Distance(actor.Position,self.Position),expected.InParty);
    }
    private Scene ReadScene()
    {
        var self=objects.LocalPlayer;
        if(self is null)return Scene.Empty;
        var members=new HashSet<ulong>();
        foreach(var member in party)if(member?.GameObject is { } obj)members.Add(obj.GameObjectId);
        var players=new List<TargetInfo>();
        foreach(var obj in objects)
            if(obj is IPlayerCharacter p && p.GameObjectId!=self.GameObjectId && p.ObjectIndex<200)
                players.Add(new(p.ObjectIndex,p.GameObjectId,Identity(p),p.ClassJob.RowId,RaceSex(p),
                    Vector3.Distance(p.Position,self.Position),members.Contains(p.GameObjectId)));
        return new(true,playerState.ContentId,players);
    }

    private void Draw()
    {
        if(!show)return;
        ImGui.SetNextWindowSize(new Vector2(650,580),ImGuiCond.FirstUseEver);
        if(!ImGui.Begin("Primal Armor · 公開測試版###PrimalArmor",ref show)){ImGui.End();return;}
        lock(gate)
        {
            if(disposed){ImGui.End();return;}
            ImGui.TextWrapped("台服 7.25 / Dalamud API 13 · 僅改變自己畫面中的其他玩家");
            ImGui.TextWrapped("首次使用請先停止舊版 SND 的 Primal Armor Auto，避免重複套用。");
            var enabled=config.Enabled;
            if(ImGui.Checkbox("啟用盔甲特效",ref enabled)){config.Enabled=enabled;manualStart=enabled;Save();}
            ImGui.SameLine();
            if(ImGui.Button("本次啟動")){config.Enabled=true;manualStart=true;Save();}
            ImGui.SameLine();
            if(ImGui.Button("停止並還原")){config.Enabled=false;Save();}
            ImGui.TextWrapped("狀態："+status);
            ImGui.TextUnformatted($"特效人數 {stats.Active} ｜ 待處理 {stats.Pending} ｜ 暫存集合 {stats.Collections}");
            ImGui.Separator();
            if(ImGui.CollapsingHeader("顯示範圍與效能",ImGuiTreeNodeFlags.DefaultOpen))
            {
                var auto=config.StartOnLogin;
                if(ImGui.Checkbox("登入後自動啟動（需已勾選啟用）",ref auto)){config.StartOnLogin=auto;Save();}
                var onlyParty=config.PartyOnly;
                if(ImGui.Checkbox("只為隊友加入盔甲效果",ref onlyParty)){config.PartyOnly=onlyParty;Save();}
                var distance=config.MaxDistance;
                if(ImGui.SliderFloat("套用距離",ref distance,5,100,"%.0f")){config.MaxDistance=distance;Save();}
                var count=config.MaxPlayers;
                if(ImGui.SliderInt("最多套用特效人數",ref count,1,200)){config.MaxPlayers=count;Save();}
                var interval=config.ProcessIntervalMs;
                if(ImGui.SliderInt("每位玩家的處理間隔（毫秒）",ref interval,50,1000)){config.ProcessIntervalMs=interval;Save();}
                ImGui.TextWrapped("距離／人數只限制新增盔甲效果；其他玩家仍會清除共用設定誤套的 Primal 盔甲效果。");
            }
            if(ImGui.CollapsingHeader("已安裝的 Primal 模組",ImGuiTreeNodeFlags.DefaultOpen))
            {
                if(ImGui.Button("重新偵測模組並重建設定"))requestScan=true;
                if(catalog is not null)
                    foreach(var job in catalog.Jobs.Values.OrderBy(j=>j.JobId))
                    {
                        var selected=config.EnabledJobs.Contains(job.JobId);
                        if(ImGui.Checkbox($"{job.JobTag}##job{job.JobId}",ref selected))
                        {
                            if(selected)config.EnabledJobs.Add(job.JobId);else config.EnabledJobs.Remove(job.JobId);
                            Save();
                        }
                        ImGui.SameLine();
                        ImGui.TextUnformatted(job.Available ? $"可用 {job.Variants.Values.Count(v=>v.Available)}/18" : "未找到相容封包");
                    }
            }
            if(ImGui.CollapsingHeader("診斷資訊"))
            {
                ImGui.TextWrapped("最近訊息："+(string.IsNullOrWhiteSpace(lastError)?"無":lastError));
                ImGui.TextUnformatted($"累計處理 {stats.Mutations} ｜ 完整重載 {stats.Redraws}");
                if(catalog is not null)
                    foreach(var issue in catalog.Issues.Take(25))ImGui.TextWrapped(issue.Message);
            }
            ImGui.Separator();
            ImGui.TextWrapped("此工具不包含 Primal 模組資源；請自行安裝原作者封包。這是尚待多人實機驗證的測試版本。");
        }
        ImGui.End();
    }

    public void Dispose()
    {
        ArmorEngine? remaining;
        ArmorEngine[] retired;
        lock(gate)
        {
            if(disposed)return;
            disposed=true;
            remaining=engine;engine=null;
            retired=pendingCleanup.ToArray();pendingCleanup.Clear();
        }
        framework.Update-=Update; pi.UiBuilder.Draw-=Draw; pi.UiBuilder.OpenConfigUi-=Open; pi.UiBuilder.OpenMainUi-=Open;
        commands.RemoveHandler("/primalarmor");
        scanCancellation.Cancel();scanCancellation.Dispose();
        if(remaining is not null || retired.Length>0)
        {
            void FinalCleanup()
            {
                remaining?.Dispose();
                foreach(var old in retired)old.Reset();
                if(remaining?.HasCleanupPending==true || retired.Any(e=>e.HasCleanupPending))
                    log.Warning("Primal Armor: 部分暫存設定未能清理，重新登入可重設。");
            }
            if(framework.IsInFrameworkUpdateThread)FinalCleanup();
            else _=framework.RunOnFrameworkThread(FinalCleanup);
        }
    }
    private sealed record Stats(int Active,int Pending,int Collections,int Mutations,int Redraws);
}
