using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrongholdObserver;

sealed record Catalog(string Version, string Sha256, Dictionary<string, string> EnemyNames,
    Dictionary<string, EffectCatalog> Effects, Dictionary<string, string> Modes,
    Dictionary<string, ModeInfo>? ModeInfo = null, Dictionary<string, EnemyBase>? EnemyBase = null,
    Dictionary<string, string>? AllyNames = null)
{
    public static Catalog Open()
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream("StrongholdObserver.catalog.json")
            ?? throw new InvalidDataException("缺少内置版本与名称目录。");
        return JsonSerializer.Deserialize<Catalog>(stream, JsonOptions.Options)
            ?? throw new InvalidDataException("无法读取版本与名称目录。");
    }

    public string EnemyName(string key) => EnemyNames.GetValueOrDefault(key) ?? key;
    public string ModeName(string key) => Modes.GetValueOrDefault(key) ?? key;
    public string AllyName(string key) => AllyNames?.GetValueOrDefault(key) ?? key;
}

sealed record ModeInfo(string? Intensity, double CreditFactor);
sealed record EnemyBase(double? MaxHp, double? Attack, double? Defense, double? Resistance, double? AttackSpeed);

sealed record EffectCatalog(string Name, string Desc, List<EffectRule> Rules);
sealed record EffectRule(string Op, Dictionary<string, JsonElement> Values, List<string> Exclude);

sealed record CurrentEffect(string Id, string Name, int Param, string Summary, string Description, IReadOnlyList<EffectRule> Rules);
sealed record EnemyInstance(string Instance, string Id, string Name, double? Hp, double? MaxHp, double? Attack, double? Defense,
    double? Resistance, double? AttackSpeed);
sealed record AllyPerformance(string Instance, string Id, string Name, double? Damage, double? Healing);
sealed record Snapshot(string Version, DateTimeOffset ReadAt, string ModeId, string Mode, int Round, IReadOnlyList<EnemyInstance> Enemies,
    IReadOnlyList<CurrentEffect> Effects, string? EnemyNote = null, string? EffectNote = null, string? Phase = null,
    [property: JsonIgnore] string? SessionKey = null, [property: JsonIgnore] bool EnemyListComplete = true,
    IReadOnlyList<AllyPerformance>? Allies = null, double? CombatSeconds = null, string? AllyNote = null, string? CombatSegment = null,
    double? TotalHealing = null);

static class JsonOptions
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}

static class Display
{
    public static string Number(double? value) => value?.ToString("#,0.##", CultureInfo.InvariantCulture) ?? "—";
    static readonly Dictionary<string, string> Labels = new() { ["atk"] = "攻击", ["max_hp"] = "生命", ["def"] = "防御", ["magic_resistance"] = "法抗", ["attack_speed"] = "攻速" };
    public static string Describe(IReadOnlyList<EffectRule> rules)
    {
        var parts = new List<string>();
        foreach (var rule in rules)
        foreach (var (key, value) in rule.Values)
        {
            if (value.ValueKind != JsonValueKind.Number) continue;
            var n = value.GetDouble();
            var op = rule.Op == "乘算" ? "×" : n < 0 ? "" : "+";
            parts.Add($"{Labels.GetValueOrDefault(key) ?? key} {op}{n:0.###}");
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
    }

    public static string Measured(EnemyInstance enemy, EnemyBase b)
    {
        var items = new[] { ("血", enemy.MaxHp, b.MaxHp), ("攻", enemy.Attack, b.Attack), ("防", enemy.Defense, b.Defense),
            ("法抗", enemy.Resistance, b.Resistance), ("攻速", enemy.AttackSpeed, b.AttackSpeed) };
        return string.Join(" · ", items.Where(x => x.Item2 != null && x.Item3 != null).Select(x =>
            $"{x.Item1} {Number(x.Item3)}→{Number(x.Item2)} ({(x.Item3 == 0 ? $"{x.Item2:+0.##;-0.##;0} 点" : $"{x.Item2 / x.Item3 - 1:+0.##%;-0.##%;0%}")})"));
    }

    public static string RuleMultipliers(IReadOnlyList<CurrentEffect> effects, string enemyId)
    {
        var factors = new Dictionary<string, double>();
        var additions = new Dictionary<string, double>();
        foreach (var effect in effects)
        foreach (var rule in effect.Rules)
        {
            if (rule.Exclude.Contains(enemyId)) continue;
            foreach (var (key, value) in rule.Values)
            {
                if (value.ValueKind != JsonValueKind.Number) continue;
                if (rule.Op == "乘算") factors[key] = factors.GetValueOrDefault(key, 1) * value.GetDouble();
                else if (rule.Op == "加值") additions[key] = additions.GetValueOrDefault(key) + value.GetDouble();
            }
        }
        return string.Join(" · ", factors.Select(kv => $"{Labels.GetValueOrDefault(kv.Key, kv.Key)} ×{kv.Value:0.###}")
            .Concat(additions.Select(kv => $"{Labels.GetValueOrDefault(kv.Key, kv.Key)} {kv.Value:+0.##;-0.##;0}")));
    }
}
