using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace PrimalArmor.Interop;

/// <summary>
/// Removes only explicitly identified Primal IMC VFX. Penumbra's own decoder,
/// boxed entry type and JSON converter preserve every other metadata field and type.
/// </summary>
public sealed class MetaNeutralizer
{
    private static RuntimeMetadata? runtime;
    private static readonly object RuntimeLock = new();

    // Existing, tested Emperor's New Hat reference, not copied from a source
    // helmet. Variants 0 and 1 are both required by the installed Primal packs.
    private const string EmperorHatJson = """
        [{"Type":"Imc","Manipulation":{"Entry":{"MaterialId":1,"DecalId":0,"VfxId":1,"MaterialAnimationId":0,"AttributeMask":1023,"SoundId":0},"ObjectType":"Equipment","PrimaryId":279,"SecondaryId":0,"Variant":0,"EquipSlot":"Head","BodySlot":"Unknown"}},{"Type":"Imc","Manipulation":{"Entry":{"MaterialId":1,"DecalId":0,"VfxId":1,"MaterialAnimationId":0,"AttributeMask":1023,"SoundId":0},"ObjectType":"Equipment","PrimaryId":279,"SecondaryId":0,"Variant":1,"EquipSlot":"Head","BodySlot":"Unknown"}}]
        """;

    public static string EmperorHatMetadata { get; } = EncodeV0(EmperorHatJson);

    /// <summary>Verifies the loaded provider without reading or modifying game state.</summary>
    public static void EnsureAvailable() => _ = GetRuntime();

    public string Clear(string encoded, IReadOnlySet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(keys);
        if (encoded.Length == 0 || keys.Count == 0) return encoded;
        var api = GetRuntime();
        object?[] arguments = [encoded, null, (byte)0];
        if (IpcBridge.Invoke(api.Decode, null, arguments) is not true || arguments[1] is not { } dictionary)
            throw new InvalidOperationException("Penumbra 無法解碼角色的中繼資料。");

        var imc = api.Imc.GetValue(dictionary) as IEnumerable
            ?? throw new InvalidOperationException("Penumbra 沒有回傳 IMC 資料。");
        var pending = new List<(object Identifier, object Entry)>();
        foreach (var pair in imc)
        {
            var identifier = api.PairKey.GetValue(pair)!;
            var entry = api.PairValue.GetValue(pair)!;
            if (keys.Contains(api.CarrierKey(identifier))
                && Convert.ToByte(api.Vfx.GetValue(entry), CultureInfo.InvariantCulture) != 0)
                pending.Add((identifier, entry));
        }
        // Avoid even a serialization roundtrip unless a whitelisted VFX changes.
        if (pending.Count == 0) return encoded;
        foreach (var (identifier, entry) in pending)
        {
            // Entry is a boxed ImcEntry struct. Mutate that copy and return it
            // through Update; all other fields and dictionary types stay intact.
            IpcBridge.Invoke(api.SetVfx, entry, [(byte)0]);
            if (IpcBridge.Invoke(api.Update, dictionary, [identifier, entry]) is not true)
                throw new InvalidOperationException("Penumbra 無法更新指定的 Primal IMC。");
        }

        var json = IpcBridge.Invoke(api.Serialize, null, [dictionary]) as string
            ?? throw new InvalidOperationException("Penumbra 無法序列化角色的中繼資料。");
        return EncodeV0(json);
    }

    private static RuntimeMetadata GetRuntime()
    {
        var provider = IpcBridge.LoadedProviderType("Penumbra", "Penumbra.Api.Api.MetaApi");
        lock (RuntimeLock)
        {
            if (runtime is null || runtime.Provider != provider) runtime = new RuntimeMetadata(provider);
            return runtime;
        }
    }

    private static string EncodeV0(string json)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.WriteByte(0);
            var bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return Convert.ToBase64String(memory.ToArray());
    }

    private sealed class RuntimeMetadata
    {
        public readonly Type Provider;
        public readonly MethodInfo Decode;
        public readonly PropertyInfo Imc;
        public readonly PropertyInfo PairKey;
        public readonly PropertyInfo PairValue;
        public readonly PropertyInfo Vfx;
        public readonly MethodInfo SetVfx;
        public readonly MethodInfo Update;
        public readonly MethodInfo Serialize;
        private readonly Func<object, object?> objectType;
        private readonly Func<object, object?> primaryId;
        private readonly Func<object, object?> secondaryId;
        private readonly Func<object, object?> variant;
        private readonly Func<object, object?> equipSlot;
        private readonly Func<object, object?> bodySlot;

        public RuntimeMetadata(Type provider)
        {
            Provider = provider;
            Decode = provider.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Single(m => m.Name == "ConvertManips" && m.GetParameters().Length == 3);
            var dictionary = Decode.GetParameters()[1].ParameterType.GetElementType()
                ?? throw new InvalidOperationException("Penumbra ConvertManips 的資料型別不符。");
            Imc = RequiredProperty(dictionary, "Imc");
            var types = Imc.PropertyType.GetGenericArguments();
            if (types.Length != 2) throw new InvalidOperationException("Penumbra IMC 的資料型別不符。");
            var identifier = types[0];
            var entry = types[1];
            var pair = typeof(KeyValuePair<,>).MakeGenericType(identifier, entry);
            PairKey = RequiredProperty(pair, "Key");
            PairValue = RequiredProperty(pair, "Value");
            Vfx = RequiredProperty(entry, "VfxId");
            SetVfx = Vfx.GetSetMethod() ?? throw new InvalidOperationException("Penumbra IMC 不支援設定 VfxId。");
            if (Vfx.PropertyType != typeof(byte)) throw new InvalidOperationException("Penumbra VfxId 的資料型別不符。");
            Update = dictionary.GetMethods().Single(m => m.Name == "Update"
                && m.GetParameters() is var p && p.Length == 2 && p[0].ParameterType == identifier && p[1].ParameterType == entry);

            var converter = dictionary.GetCustomAttributesData()
                .SingleOrDefault(a => a.AttributeType.FullName == "Newtonsoft.Json.JsonConverterAttribute")
                ?? throw new InvalidOperationException("找不到 Penumbra 的中繼資料 JSON 轉換器。");
            var json = converter.AttributeType.Assembly.GetType("Newtonsoft.Json.JsonConvert", throwOnError: true)!;
            Serialize = json.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m =>
                m.Name == "SerializeObject" && m.GetParameters() is var p && p.Length == 1 && p[0].ParameterType == typeof(object));

            objectType = Getter(identifier, "ObjectType");
            primaryId = IdGetter(identifier, "PrimaryId");
            secondaryId = IdGetter(identifier, "SecondaryId");
            variant = IdGetter(identifier, "Variant");
            equipSlot = Getter(identifier, "EquipSlot");
            bodySlot = Getter(identifier, "BodySlot");
        }

        public string CarrierKey(object identifier)
            => string.Join(":", Text(objectType(identifier)), Text(primaryId(identifier)), Text(secondaryId(identifier)),
                Text(variant(identifier)), Text(equipSlot(identifier)), Text(bodySlot(identifier)));

        private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        private static Func<object, object?> IdGetter(Type identifier, string name)
        {
            var outer = RequiredProperty(identifier, name);
            var inner = Getter(outer.PropertyType, "Id");
            return value => inner(outer.GetValue(value)!);
        }

        private static Func<object, object?> Getter(Type type, string name)
        {
            if (type.GetProperty(name) is { } property) return value => property.GetValue(value);
            if (type.GetField(name) is { } field) return value => field.GetValue(value);
            throw new InvalidOperationException($"找不到 Penumbra 的 {type.Name}.{name}。");
        }

        private static PropertyInfo RequiredProperty(Type type, string name)
            => type.GetProperty(name) ?? throw new InvalidOperationException($"找不到 Penumbra 的 {type.Name}.{name}。");
    }
}
