using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StrongholdObserver;

sealed class LiveReader(IReadMemory mem, ulong module, Catalog catalog, string processIdentity = "fixture")
{
    // V3_1 PC layout. Validate runtime objects before sampling; all offsets are read-only.
    internal const ulong ControllerType = 0x8B0FC40;
    internal const ulong ObscuredLongType = 0x8B254B8;
    internal const ulong Parent = 0x58, StaticFields = 0xB8, ClassName = 0x10;
    internal const ulong ControllerHost = 0x2E8, ControllerUnits = 0x2C0, HostDictionary = 0x10;
    internal const ulong DictionaryEntries = 0x18, DictionarySize = 0x20, DictionaryVersion = 0x2C, EntrySize = 0x18, EntryValue = 0x10;
    internal const ulong ManagerDataCenter = 0x40, ManagerStart = 0x10, ManagerPhase = 0x20, Activity = 0x10, Round = 0x3C, Effects = 0x48, Mode = 0x50;
    internal const ulong ListItems = 0x10, ListSize = 0x18, ListVersion = 0x1C, ArrayLength = 0x18, ArrayData = 0x20;
    internal const ulong EffectId = 0x10, EffectParam = 0x18;
    internal const ulong EnemyArray = 0x20, EnemyArrayItems = 0x10, EnemyArraySize = 0x20;
    internal const ulong EnemyUid = 0x18, EnemyBorn = 0x25, EnemyHp = 0x40, EnemyAttributes = 0xB0, EnemyId = 0x148, EnemyDummy = 0x268;
    internal const ulong Cached = 0x68, DirtyMask = 0x78, ObscuredStride = 40;
    internal const ulong ControllerLogger = 0xE0, LoggerStats = 0x30, StatsCharacters = 0x30, StatDamage = 0x20, StatDamageTypes = 0x38;
    internal const ulong Deterministic = 0x10, FixedPlayTime = 0x18, FixedPlayTimeIgnored = 0x20;
    internal const double FixedOne = 4294967296d;
    readonly Dictionary<ulong, string> _names = new();

    public Snapshot Read()
    {
        var controller = Controller();
        var gm = FindGameManager(controller);
        var data = mem.Pointer(gm + ManagerDataCenter, "卫戍对局数据");
        var start = mem.U64(gm + ManagerStart);
        var phaseCode = mem.I32(gm + ManagerPhase);
        var phase = phaseCode switch { 1 => "加载", 2 => "机变", 3 => "整备", 4 => "作战", 5 => "联防", 6 => "领袖战", 7 => "结算", 8 => "结束", 9 => "暂停", _ => null };
        if (phaseCode == 1) throw new NoSession("正在加载对局；之前的波次记录仍保留。");
        var activity = mem.ManagedString(mem.U64(data + Activity));
        if (activity != "act3autochess") throw new NoSession("当前未进入「卫戍协议·规程·序列 I」的战斗场景。");
        var modeId = mem.ManagedString(mem.U64(data + Mode));
        var round = mem.I32(data + Round);
        if (modeId == null || !catalog.Modes.ContainsKey(modeId) || round is < 1 or > 30)
            throw new NoSession("已找到卫戍场景，正在等待对局与回合数据。");
        var effects = ReadEffects(data, out var effectNote);
        var enemies = ReadEnemies(controller, out var enemyNote, out var listComplete);
        var allies = ReadAllies(controller, phaseCode, out var combatSeconds, out var segment, out var allyNote);
        if (mem.U64(gm + ManagerDataCenter) != data || mem.I32(data + Round) != round || mem.I32(gm + ManagerPhase) != phaseCode)
            throw new NoSession("回合正在切换，等待下一帧数据。");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{processIdentity}:{gm:X}:{data:X}:{start:X}")));
        return new Snapshot(catalog.Version, DateTimeOffset.Now, modeId, catalog.ModeName(modeId), round, enemies, effects, enemyNote, effectNote, phase, key, listComplete,
            allies, combatSeconds, allyNote, segment);
    }

    ulong Controller()
    {
        var klass = mem.Pointer(module + ControllerType, "战斗模块");
        if (GetClassName(klass) != "BattleController") throw new NoSession("战斗场景尚未初始化，或当前客户端的战斗数据布局不兼容。");
        var singleton = mem.Pointer(klass + Parent, "战斗场景类");
        var fields = mem.Pointer(singleton + StaticFields, "战斗场景");
        var controller = mem.Pointer(fields, "战斗场景");
        if (mem.U64(controller) != klass) throw new NoSession("当前没有活动的战斗场景。");
        return controller;
    }

    string? GetClassName(ulong klass)
    {
        if (!Mem.IsPointer(klass)) return null;
        if (_names.TryGetValue(klass, out var value)) return value;
        var s = mem.CString(mem.U64(klass + ClassName));
        if (s != null && _names.Count < 1000) _names[klass] = s;
        return s;
    }

    ulong FindGameManager(ulong controller)
    {
        var host = mem.Pointer(controller + ControllerHost, "对局对象");
        var dictionary = mem.Pointer(host + HostDictionary, "对局对象表");
        var entries = mem.Pointer(dictionary + DictionaryEntries, "对局对象表");
        var count = mem.I32(dictionary + DictionarySize);
        var version = mem.I32(dictionary + DictionaryVersion);
        var length = mem.U64(entries + ArrayLength);
        if (count is < 1 or > 512 || (ulong)count > length || length > 4096) throw new NoSession("战斗对象表尚未就绪。");
        var b = mem.Read(entries + ArrayData, checked(count * (int)EntrySize));
        for (var i = 0; i < count; i++)
        {
            var offset = i * (int)EntrySize;
            if (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset)) < 0) continue;
            var candidate = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(offset + (int)EntryValue));
            if (!Mem.IsPointer(candidate)) continue;
            try
            {
                if (GetClassName(mem.U64(candidate)) == "AutoChessGameManager")
                {
                    if (mem.I32(dictionary + DictionaryVersion) != version) throw new NoSession("对局对象正在更新。");
                    return candidate;
                }
            }
            catch (ReadFault) { /* Other singleton entries can be released while reading. */ }
        }
        throw new NoSession("游戏已连接，等待进入卫戍协议对局。");
    }

    List<CurrentEffect> ReadEffects(ulong data, out string? note)
    {
        note = null;
        var list = mem.U64(data + Effects);
        if (!Mem.IsPointer(list)) { note = "当前回合效果列表尚未下发。"; return []; }
        try
        {
            var count = mem.I32(list + ListSize);
            var ver = mem.I32(list + ListVersion);
            var items = count == 0 ? Array.Empty<ulong>() : ReadObjectArray(mem.Pointer(list + ListItems, "强化列表"), count, 512);
            var rows = new List<CurrentEffect>(items.Length);
            foreach (var entry in items)
            {
                if (!Mem.IsPointer(entry)) throw new ReadFault("强化项已切换。");
                var id = mem.ManagedString(mem.U64(entry + EffectId));
                if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id)) throw new ReadFault("强化编号无效。");
                var param = mem.I32(entry + EffectParam);
                var display = catalog.Effects.GetValueOrDefault(id);
                var rules = display?.Rules ?? [];
                rows.Add(new CurrentEffect(id, display?.Name ?? id, param, Display.Describe(rules), display?.Desc ?? "本地名称目录没有该效果。", rules));
            }
            if (mem.I32(list + ListVersion) != ver) throw new ReadFault("强化列表正在更新。");
            return rows;
        }
        catch (Exception e) when (e is ReadFault or NoSession) { note = "强化列表本帧未读完整；等待下一帧。"; return []; }
    }

    List<EnemyInstance> ReadEnemies(ulong controller, out string? note, out bool complete)
    {
        note = null;
        complete = false;
        try
        {
            var manager = mem.Pointer(controller + ControllerUnits, "在场单位管理器");
            var collection = mem.Pointer(manager + EnemyArray, "在场敌人列表");
            var count = mem.I32(collection + EnemyArraySize);
            if (count == 0) { complete = true; note = "当前没有在场敌人。"; return []; }
            var items = ReadObjectArray(mem.Pointer(collection + EnemyArrayItems, "敌人列表数组"), count, 1024);
            var rows = new List<EnemyInstance>();
            var seen = new HashSet<string>();
            var skipped = false;
            foreach (var entity in items)
            {
                if (!Mem.IsPointer(entity)) { skipped = true; continue; }
                try
                {
                    if (mem.U8(entity + EnemyBorn) != 1 || mem.U8(entity + EnemyDummy) != 0) continue;
                    var hp = ToNumber(mem.I64(entity + EnemyHp));
                    if (hp < 0 || hp >= 1_000_000_000) continue;
                    var idPointer = mem.U64(entity + EnemyId);
                    var id = mem.ManagedString(idPointer);
                    if (id == null || !id.StartsWith("enemy_", StringComparison.Ordinal) || !IdPattern.IsMatch(id)) continue;
                    var uid = unchecked((uint)mem.I32(entity + EnemyUid));
                    var key = uid != 0 ? uid.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{entity:X}";
                    if (!seen.Add(key)) continue;
                    var current = ReadStats(entity);
                    if (mem.U64(entity + EnemyId) != idPointer || unchecked((uint)mem.I32(entity + EnemyUid)) != uid) { skipped = true; continue; }
                    rows.Add(new EnemyInstance(key, id, catalog.EnemyName(id), hp, current.Hp, current.Atk, current.Def, current.Res, current.Aspd));
                }
                catch (ReadFault) { skipped = true; }
            }
            if (mem.I32(collection + EnemyArraySize) != count) throw new ReadFault("敌人列表正在变化。");
            complete = !skipped;
            if (skipped) note = $"本帧读到 {rows.Count} 个实例；生成或退场中的对象将在下一帧重读。";
            else if (rows.Count == 0) note = "当前没有可读的存活敌人。";
            return rows;
        }
        catch (Exception ex) when (ex is ReadFault or NoSession) { note = "在场敌人列表本帧尚未就绪；等待下一帧。"; return []; }
    }

    List<AllyPerformance>? ReadAllies(ulong controller, int phaseCode, out double? seconds, out string? segment, out string? note)
    {
        seconds = null;
        segment = null;
        note = null;
        if (phaseCode is not (4 or 5 or 6 or 9)) return null;
        try
        {
            var logger = mem.Pointer(controller + ControllerLogger, "战报记录器");
            var stats = mem.Pointer(logger + LoggerStats, "战报统计对象");
            var list = mem.Pointer(stats + StatsCharacters, "干员战报列表");
            var count = mem.I32(list + ListSize);
            var version = mem.I32(list + ListVersion);
            if (count < 0 || count > 512) throw new ReadFault("战报列表长度无效。");
            var clockClass = mem.Pointer(module + ControllerType, "战斗计时类型");
            var clock = mem.Pointer(clockClass + StaticFields, "战斗计时字段");
            if (mem.U8(clock + Deterministic) == 1)
            {
                var t = ToNumber(mem.I64(clock + FixedPlayTime)) - ToNumber(mem.I64(clock + FixedPlayTimeIgnored));
                if (double.IsFinite(t) && t >= 0 && t <= 7200) seconds = t;
                if (seconds == 0) { note = "战斗计时刚开始，等待本阶段战报刷新。"; return null; }
            }
            if (count == 0)
            {
                note = "游戏还没有记录干员的输出或治疗。";
                return null;
            }
            var array = mem.Pointer(list + ListItems, "干员战报数组");
            if (mem.U64(array + ArrayLength) < (ulong)count) throw new ReadFault("干员战报容量无效。");
            var entries = mem.Read(array + ArrayData, count * 16);
            var results = new List<AllyPerformance>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var id = mem.ManagedString(BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(i * 16)));
                var value = BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(i * 16 + 8));
                if (id == null || !id.StartsWith("char_", StringComparison.Ordinal) || !IdPattern.IsMatch(id) || !seen.Add(id)) continue;
                if (!Mem.IsPointer(value)) throw new ReadFault("干员战报条目正在更新。");
                var damage = Nonnegative(mem.F32(value + StatDamage));
                double? heal = null;
                var byType = mem.U64(value + StatDamageTypes);
                if (Mem.IsPointer(byType) && mem.I32(byType + ListSize) is >= 5 and <= 256)
                {
                    var bucket = mem.U64(byType + ListItems);
                    if (Mem.IsPointer(bucket) && mem.U64(bucket + ArrayLength) >= 5)
                        heal = Nonnegative(mem.F32(bucket + ArrayData + 4 * sizeof(float)));
                }
                results.Add(new AllyPerformance(id, id, catalog.AllyName(id), damage, heal));
            }
            if (mem.I32(list + ListVersion) != version || mem.U64(logger + LoggerStats) != stats)
                throw new ReadFault("干员战报列表正在更新。");
            if (results.Count == 0) { note = "这个战斗阶段还没有可读的干员条目。"; return null; }
            segment = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{processIdentity}:{controller:X}:{stats:X}")));
            if (seconds == null) note = "这帧没有可用的确定性战斗计时；累计值仍可读。";
            return results;
        }
        catch (Exception ex) when (ex is ReadFault or NoSession)
        {
            seconds = null;
            segment = null;
            note = "干员战报本帧未读完整；保留已观测到的累计值。";
            return null;
        }
    }

    static double? Nonnegative(float n) => float.IsFinite(n) && Math.Abs((double)n) < 1e12 ? Math.Abs((double)n) : null;

    (double? Hp, double? Atk, double? Def, double? Res, double? Aspd) ReadStats(ulong entity)
    {
        var attrs = mem.U64(entity + EnemyAttributes);
        if (!Mem.IsPointer(attrs)) return default;
        var array = mem.U64(attrs + Cached);
        if (!Mem.IsPointer(array)) return default;
        var dirty = mem.U64(attrs + DirtyMask);
        var len = mem.U64(array + ArrayLength);
        double? Current(int index)
        {
            if ((ulong)index >= len || (dirty & (1UL << index)) != 0) return null;
            return ReadCachedAttribute(array + ArrayData + (ulong)index * ObscuredStride);
        }
        var result = (Current(0), Current(1), Current(2), Current(3), Current(7));
        return mem.U64(attrs + DirtyMask) == dirty ? result : default;
    }

    double? ReadCachedAttribute(ulong at)
    {
        // Non-initialized cache entries are unavailable, not zero-valued stats.
        var b = mem.Read(at, (int)ObscuredStride);
        if (b[0x10] == 0) return null;
        var key = BinaryPrimitives.ReadInt64LittleEndian(b);
        if (key == 0)
        {
            var klass = mem.U64(module + ObscuredLongType);
            if (GetClassName(klass) != "ObscuredLong") return null;
            var fields = mem.U64(klass + StaticFields);
            if (!Mem.IsPointer(fields)) return null;
            key = mem.I64(fields);
        }
        var n = key ^ BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(8));
        var value = ToNumber(n);
        return double.IsFinite(value) && Math.Abs(value) < 1_000_000_000 ? value : null;
    }

    ulong[] ReadObjectArray(ulong array, int count, int max)
    {
        var size = mem.U64(array + ArrayLength);
        if (count < 0 || count > max || (ulong)count > size || size > (ulong)max * 4) throw new ReadFault("列表长度不在预期范围。");
        if (count == 0) return [];
        var b = mem.Read(array + ArrayData, checked(count * 8));
        var rows = new ulong[count];
        for (var i = 0; i < rows.Length; i++) rows[i] = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i * 8));
        return rows;
    }

    static double ToNumber(long value) => value / FixedOne;
    static readonly Regex IdPattern = new("^[a-zA-Z0-9_@./#-]{1,160}$", RegexOptions.CultureInvariant);
}

sealed class ObserverSession(Catalog catalog) : IDisposable
{
    readonly object _gate = new();
    ProcessMemory? _memory;
    LiveReader? _reader;
    public Snapshot Sample()
    {
        lock (_gate)
        {
            if (_memory != null && !_memory.Running) Clear();
            if (_memory == null)
            {
                _memory = ProcessMemory.Attach();
                _reader = new LiveReader(_memory, _memory.AssemblyBase, catalog, _memory.Identity);
            }
            return _reader!.Read();
        }
    }
    void Clear() { _memory?.Dispose(); _memory = null; _reader = null; }
    public void Dispose() { lock (_gate) Clear(); }
}
