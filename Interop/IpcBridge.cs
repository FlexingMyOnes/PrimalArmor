using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace PrimalArmor.Interop;

public readonly record struct CollectionInfo(bool Valid, bool Individual, Guid Id, string Name);
public readonly record struct HeadState(ulong ItemId, byte[] Stains, bool HatVisible);

/// <summary>The engine's IPC boundary; its implementation only runs on the framework thread.</summary>
public interface IArmorInterop
{
    bool TryConnect(out string error);
    CollectionInfo GetCollection(int index);
    string ResolvePath(string path, int index);
    string GetMeta(int index);
    Guid CreateCollection(string name);
    void DeleteCollection(Guid id);
    void AssignCollection(Guid id, int index);
    void AddMod(string tag, Guid id, Dictionary<string, string> files, string meta, int priority);
    void Redraw(int index);
    HeadState ReadHead(int index);
    void SetHead(int index, ulong itemId, byte[] stains);
    void SetHatVisible(int index, bool visible);
}

/// <summary>
/// IPC contracts verified against the installed TW 7.25 providers. There are no
/// Penumbra, Glamourer, or Newtonsoft compile-time references. In particular the
/// two provider-owned return types retain their original AssemblyLoadContext.
/// </summary>
public sealed class IpcBridge(IDalamudPluginInterface pluginInterface, IFramework framework) : IArmorInterop
{
    private const byte HeadSlot = 3;
    private const ulong HatFlag = 2;
    private const ulong OnceFlag = 1;
    private Connection? connection;
    private bool reconnectRequired = true;

    public bool TryConnect(out string error)
    {
        try
        {
            RequireFrameworkThread();
            foreach (var name in new[] { "Penumbra", "Glamourer" })
                if (!pluginInterface.InstalledPlugins.Any(p => p.InternalName == name && p.IsLoaded))
                    throw new InvalidOperationException($"請先啟用 {name}。");

            if (!reconnectRequired && connection is { } existing)
            {
                existing.RequireAvailable();
                error = string.Empty;
                return true;
            }

            // Do not decode or modify anything here; fail closed if this
            // provider version cannot preserve all of the metadata types.
            MetaNeutralizer.EnsureAvailable();
            var candidate = new Connection(pluginInterface);
            candidate.RequireAvailable();
            connection = candidate;
            reconnectRequired = false;
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            // Retain the last endpoints for best-effort cleanup. For example,
            // Penumbra collection deletion still works if Glamourer unloads.
            // The next successful readiness check rebinds provider-owned types.
            reconnectRequired = true;
            error = Unwrap(exception).Message;
            return false;
        }
    }

    public CollectionInfo GetCollection(int index)
    {
        var result = Ready().GetCollection.InvokeFunc(index);
        return new CollectionInfo(result.Item1, result.Item2, result.Item3.Item1, result.Item3.Item2 ?? string.Empty);
    }

    public string ResolvePath(string path, int index) => Ready().Resolve.InvokeFunc(path, index) ?? string.Empty;
    public string GetMeta(int index) => Ready().GetMeta.InvokeFunc(index) ?? string.Empty;

    public Guid CreateCollection(string name)
    {
        var tuple = AsTuple(Ready().Create.Invoke("PrimalArmor", name), "建立暫存集合");
        RequireSuccess(tuple[0], "建立暫存集合");
        if (tuple[1] is not Guid id || id == Guid.Empty)
            throw new InvalidOperationException("Penumbra 回傳了無效的暫存集合。");
        return id;
    }

    public void DeleteCollection(Guid id)
    {
        var code = Ready().Delete.InvokeFunc(id);
        // NothingChanged (1) and CollectionMissing (2) are both idempotent
        // cleanup. The installed API returns 2 for a collection already absent.
        if (code is not (0 or 1 or 2))
            RequireSuccess(code, "移除暫存集合");
    }

    public void AssignCollection(Guid id, int index)
        => RequireSuccess(Ready().Assign.InvokeFunc(id, index, true), "分配暫存集合");

    public void AddMod(string tag, Guid id, Dictionary<string, string> files, string meta, int priority)
        => RequireSuccess(Ready().Add.InvokeFunc(tag, id, files, meta, priority), "加入暫存資源");

    public void Redraw(int index) => Ready().Redraw.InvokeAction(index, 0);

    public HeadState ReadHead(int index)
    {
        var tuple = AsTuple(Ready().GetState.Invoke(index, 0u), "讀取頭部外觀");
        RequireSuccess(tuple[0], "讀取頭部外觀");
        var providerJson = tuple[1] ?? throw new InvalidOperationException("Glamourer 沒有回傳角色資料。");
        // JObject belongs to Glamourer's Newtonsoft copy. Crossing the IPC
        // boundary as its exact runtime type and reading JSON text avoids casts
        // to a different plugin's Newtonsoft assembly.
        using var document = JsonDocument.Parse(providerJson.ToString() ?? string.Empty);
        if (!TryPath(document.RootElement, out var item, "Equipment", "Head", "ItemId"))
            throw new InvalidOperationException("Glamourer 的角色資料沒有頭部裝備。");
        var itemId = item.ValueKind == JsonValueKind.String
            ? ulong.Parse(item.GetString()!, CultureInfo.InvariantCulture)
            : item.GetUInt64();
        var stains = new[] { ReadStain(document.RootElement, "Stain"), ReadStain(document.RootElement, "Stain2") };
        var visible = !TryPath(document.RootElement, out var hat, "Equipment", "Hat", "Show") || hat.GetBoolean();
        return new HeadState(itemId, stains, visible);
    }

    public void SetHead(int index, ulong itemId, byte[] stains)
    {
        if (stains is null || stains.Length != 2)
            throw new ArgumentException("A head appearance requires exactly two stain channels.", nameof(stains));
        RequireSuccess(Ready().SetItem.InvokeFunc(index, HeadSlot, itemId, stains, 0u, OnceFlag), "設定頭部外觀");
    }

    public void SetHatVisible(int index, bool visible)
        => RequireSuccess(Ready().SetMeta.InvokeFunc(index, HatFlag, visible, 0u, OnceFlag), "設定帽子顯示");

    private Connection Ready()
    {
        RequireFrameworkThread();
        return connection ?? throw new InvalidOperationException("Penumbra / Glamourer IPC 尚未連線。");
    }

    private void RequireFrameworkThread()
    {
        if (!framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Primal Armor IPC 必須在遊戲 Framework 執行緒執行。");
    }

    private static byte ReadStain(JsonElement root, string name)
    {
        if (!TryPath(root, out var value, "Equipment", "Head", name)) return 0;
        return value.ValueKind == JsonValueKind.String
            ? byte.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : value.GetByte();
    }

    private static bool TryPath(JsonElement root, out JsonElement value, params string[] names)
    {
        value = root;
        foreach (var name in names)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var next))
                return false;
            value = next;
        }
        return true;
    }

    private static ITuple AsTuple(object? value, string operation)
        => value is ITuple { Length: 2 } tuple ? tuple
            : throw new InvalidOperationException($"{operation}：IPC 回傳格式不符。");

    private static void RequireSuccess(object? code, string operation)
    {
        if (code is not null && Convert.ToInt64(code, CultureInfo.InvariantCulture) == 0) return;
        throw new InvalidOperationException($"{operation}：{code ?? "無回傳值"}");
    }

    internal static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: { } inner }) exception = inner;
        return exception;
    }

    internal static object? Invoke(MethodInfo method, object? target, object?[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    internal static Type LoadedProviderType(string assemblyName, string typeName)
    {
        // A reloaded plugin is enumerated after its old, unloading assembly.
        // Never Assembly.LoadFrom a provider or copy its API into this plugin.
        var assembly = AppDomain.CurrentDomain.GetAssemblies().LastOrDefault(a => a.GetName().Name == assemblyName)
            ?? throw new InvalidOperationException($"尚未載入 {assemblyName}。");
        return assembly.GetType(typeName, throwOnError: true)!;
    }

    private sealed class Connection
    {
        public readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> GetCollection;
        public readonly ICallGateSubscriber<string, int, string> Resolve;
        public readonly ICallGateSubscriber<int, string> GetMeta;
        public readonly RuntimeCall Create;
        public readonly ICallGateSubscriber<Guid, int> Delete;
        public readonly ICallGateSubscriber<Guid, int, bool, int> Assign;
        public readonly ICallGateSubscriber<string, Guid, Dictionary<string, string>, string, int, int> Add;
        public readonly ICallGateSubscriber<int, int, object> Redraw;
        public readonly RuntimeCall GetState;
        public readonly ICallGateSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int> SetItem;
        public readonly ICallGateSubscriber<int, ulong, bool, uint, ulong, int> SetMeta;
        private readonly (string Label, Func<bool> Available)[] gates;

        public Connection(IDalamudPluginInterface pi)
        {
            GetCollection = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
            Resolve = pi.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath");
            GetMeta = pi.GetIpcSubscriber<int, string>("Penumbra.GetMetaManipulations.V5");
            Create = RuntimeCall.ForProvider(pi, "Penumbra.CreateTemporaryCollection.V6", "Penumbra",
                "Penumbra.Api.Api.TemporaryApi", "CreateTemporaryCollection", false, typeof(string), typeof(string));
            Delete = pi.GetIpcSubscriber<Guid, int>("Penumbra.DeleteTemporaryCollection.V5");
            Assign = pi.GetIpcSubscriber<Guid, int, bool, int>("Penumbra.AssignTemporaryCollection.V5");
            Add = pi.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5");
            Redraw = pi.GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5");
            GetState = RuntimeCall.ForProvider(pi, "Glamourer.GetState", "Glamourer",
                "Glamourer.Api.StateApi", "GetState", true, typeof(int), typeof(uint));
            SetItem = pi.GetIpcSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int>("Glamourer.SetItem.V3");
            SetMeta = pi.GetIpcSubscriber<int, ulong, bool, uint, ulong, int>("Glamourer.SetMetaState");
            // API13's high-arity subscriber interfaces omit the common base.
            // Read registration state through a one-type subscriber to the
            // same named gate; it is never used to invoke the provider.
            var addReady = pi.GetIpcSubscriber<object>("Penumbra.AddTemporaryMod.V5");
            var setItemReady = pi.GetIpcSubscriber<object>("Glamourer.SetItem.V3");
            var setMetaReady = pi.GetIpcSubscriber<object>("Glamourer.SetMetaState");
            gates =
            [
                ("Penumbra.GetCollectionForObject.V5", () => GetCollection.HasFunction),
                ("Penumbra.ResolveGameObjectPath", () => Resolve.HasFunction),
                ("Penumbra.GetMetaManipulations.V5", () => GetMeta.HasFunction),
                ("Penumbra.CreateTemporaryCollection.V6", () => Create.Gate.HasFunction),
                ("Penumbra.DeleteTemporaryCollection.V5", () => Delete.HasFunction),
                ("Penumbra.AssignTemporaryCollection.V5", () => Assign.HasFunction),
                ("Penumbra.AddTemporaryMod.V5", () => addReady.HasFunction),
                ("Penumbra.RedrawObject.V5", () => Redraw.HasAction),
                ("Glamourer.GetState", () => GetState.Gate.HasFunction),
                ("Glamourer.SetItem.V3", () => setItemReady.HasFunction),
                ("Glamourer.SetMetaState", () => setMetaReady.HasFunction),
            ];
        }

        public void RequireAvailable()
        {
            foreach (var (label, available) in gates)
                if (!available())
                    throw new InvalidOperationException($"IPC 尚未就緒：{label}");
        }
    }

    private sealed class RuntimeCall(object target, MethodInfo invoke, ICallGateSubscriber gate)
    {
        public ICallGateSubscriber Gate { get; } = gate;
        public object? Invoke(params object?[] arguments) => IpcBridge.Invoke(invoke, target, arguments);

        public static RuntimeCall ForProvider(IDalamudPluginInterface pi, string label, string assemblyName,
            string typeName, string methodName, bool integerStatus, params Type[] parameters)
        {
            var provider = LoadedProviderType(assemblyName, typeName);
            var method = provider.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Single(m => m.Name == methodName && m.GetParameters().Length == parameters.Length);
            // Glamourer's public method returns (GlamourerApiEc, JObject), but
            // its IPC wrapper deliberately converts the status to Int32.
            // Penumbra V6's creation endpoint keeps its provider enum instead.
            var result = integerStatus
                ? typeof(ValueTuple<,>).MakeGenericType(typeof(int), method.ReturnType.GetGenericArguments()[1])
                : method.ReturnType;
            var signature = parameters.Append(result).ToArray();
            var subscriberFactory = typeof(IDalamudPluginInterface).GetMethods()
                .Single(m => m.Name == nameof(IDalamudPluginInterface.GetIpcSubscriber)
                    && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == signature.Length)
                .MakeGenericMethod(signature);
            var subscriber = IpcBridge.Invoke(subscriberFactory, pi, [label])
                ?? throw new InvalidOperationException($"找不到 IPC：{label}");
            var invoke = subscriberFactory.ReturnType.GetMethod("InvokeFunc")
                ?? throw new InvalidOperationException($"找不到 IPC 函式：{label}");
            return new RuntimeCall(subscriber, invoke, (ICallGateSubscriber)subscriber);
        }
    }
}
