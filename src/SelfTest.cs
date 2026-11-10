using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace StrongholdObserver;

static class SelfTest
{
    public static void Run()
    {
        var start = new ProcessStartInfo { FileName = Environment.ProcessPath ?? "dotnet", UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "StrongholdObserver.dll"));
        start.ArgumentList.Add("--test-host");
        using (var child = Process.Start(start) ?? throw new InvalidOperationException("Controlled target failed to start."))
        {
            try
            {
                var line = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                var parts = line?.Split(':') ?? [];
                Check(parts.Length == 2 && parts[0] == child.Id.ToString(), "controlled target identity");
                var marker = ProcessMemory.ReadMarker(child.Id, Convert.ToUInt64(parts[1], 16));
                Check(marker == ("STRONGHOLD-READ", 0), "ReadProcessMemory separate control process");
            }
            finally
            {
                child.StandardInput.Close();
                if (!child.WaitForExit(1000)) child.Kill();
            }
        }

        var catalog = Catalog.Open();
        Check(catalog.EnemyName("enemy_1216_cansld") == "寻路者阵地破坏者", "enemy name");
        Check(catalog.ModeName("mode_multi_abyss") == "终极模拟", "mode name");
        var m = new Fixture();
        var module = 0x180000000UL;
        var klass = m.New(); m.Put(module + LiveReader.ControllerType, klass); m.Put(klass + LiveReader.ClassName, m.C("BattleController"));
        var parent = m.New(); m.Put(klass + LiveReader.Parent, parent);
        var sf = m.New(); m.Put(parent + LiveReader.StaticFields, sf);
        var bc = m.New(); m.Put(sf, bc); m.Put(bc, klass);
        var host = m.New(); m.Put(bc + LiveReader.ControllerHost, host);
        var dict = m.New(); m.Put(host + LiveReader.HostDictionary, dict);
        var entries = m.New(); m.Put(dict + LiveReader.DictionaryEntries, entries); m.Int(dict + LiveReader.DictionarySize, 2); m.Put(entries + LiveReader.ArrayLength, 2);
        var other = m.New(); m.Put(entries + LiveReader.ArrayData + LiveReader.EntryValue, other); m.Class(other, "OtherSingleton");
        var gm = m.New(); m.Put(entries + LiveReader.ArrayData + LiveReader.EntrySize + LiveReader.EntryValue, gm); m.Class(gm, "AutoChessGameManager");
        var dc = m.New(); m.Put(gm + LiveReader.ManagerDataCenter, dc); m.Int(gm + LiveReader.ManagerPhase, 4); m.Put(dc + LiveReader.Activity, m.S("act3autochess")); m.Put(dc + LiveReader.Mode, m.S("mode_multi_abyss")); m.Int(dc + LiveReader.Round, 13);
        var ef = m.New(); m.Put(dc + LiveReader.Effects, ef); m.Int(ef + LiveReader.ListSize, 1); m.Int(ef + LiveReader.ListVersion, 9);
        var entry = m.New(); m.Put(ef + LiveReader.ListItems, m.Array(entry)); m.Put(entry + LiveReader.EffectId, m.S("aceffect_enemy_6")); m.Int(entry + LiveReader.EffectParam, 3);

        var u = m.New(); var dummy = m.New(); var dead = m.New();
        var um = m.New(); m.Put(bc + LiveReader.ControllerUnits, um);
        var collection = m.New(); m.Put(um + LiveReader.EnemyArray, collection); m.Int(collection + LiveReader.EnemyArraySize, 3);
        m.Put(collection + LiveReader.EnemyArrayItems, m.Array(u, dummy, dead));
        foreach (var enemy in new[] { u, dummy, dead })
        {
            m.Byte(enemy + LiveReader.EnemyBorn, 1);
            m.Put(enemy + LiveReader.EnemyId, m.S("enemy_1216_cansld"));
            m.Put(enemy + LiveReader.EnemyHp, 20150UL << 32);
        }
        m.Int(u + LiveReader.EnemyUid, 1234);
        m.Int(dead + LiveReader.EnemyUid, 5678);
        m.Byte(dummy + LiveReader.EnemyDummy, 1);
        m.Put(dead + LiveReader.EnemyHp, 0);
        var attrs = m.New(); m.Put(u + LiveReader.EnemyAttributes, attrs);
        var arr = m.New(); m.Put(attrs + LiveReader.Cached, arr); m.Put(arr + LiveReader.ArrayLength, 8); m.Put(attrs + LiveReader.DirtyMask, 1UL << 2);
        m.Fixed(arr, 0, 28640);
        m.Fixed(arr, 1, 1483.75);
        m.Fixed(arr, 2, 1200);
        m.Fixed(arr, 3, 5);
        m.Fixed(arr, 7, 120);
        var clock = m.New(); m.Put(klass + LiveReader.StaticFields, clock); m.Byte(clock + LiveReader.Deterministic, 1);
        m.Put(clock + LiveReader.FixedPlayTime, 29UL << 32); m.Put(clock + LiveReader.FixedPlayTimeIgnored, 4UL << 32);
        var logger = m.New(); var stats = m.New(); var statsList = m.New(); var statsArray = m.New(); var actor = m.New();
        m.Put(bc + LiveReader.ControllerLogger, logger); m.Put(logger + LiveReader.LoggerStats, stats);
        m.Put(stats + LiveReader.StatsCharacters, statsList); m.Put(statsList + LiveReader.ListItems, statsArray);
        m.Int(statsList + LiveReader.ListSize, 1); m.Put(statsArray + LiveReader.ArrayLength, 1);
        m.Put(statsArray + LiveReader.ArrayData, m.S("char_002_amiya")); m.Put(statsArray + LiveReader.ArrayData + 8, actor);
        m.Float(actor + LiveReader.StatDamage, -2500);
        var byType = m.New(); var bucket = m.New(); m.Put(actor + LiveReader.StatDamageTypes, byType);
        m.Int(byType + LiveReader.ListSize, 5); m.Put(byType + LiveReader.ListItems, bucket);
        m.Put(bucket + LiveReader.ArrayLength, 5); m.Float(bucket + LiveReader.ArrayData + 16, 900);

        var reader = new LiveReader(m, module, catalog);
        var s = reader.Read();
        Check(s.Round == 13 && s.Mode == "终极模拟" && s.Phase == "作战", "mode and current round");
        Check(s.Effects.Count == 1 && s.Effects[0].Param == 3 && s.Effects[0].Summary.Contains("生命 ×1.2"), "current effect and its lookup");
        Check(s.Enemies.Count == 2 && s.Enemies.Single(e => e.Instance == "5678").Hp == 0, "explicit zero HP retained, preview filtered");
        var row = s.Enemies.Single(e => e.Instance == "1234");
        Check(row.Hp == 20150 && row.MaxHp == 28640 && row.Attack == 1483.75 && row.Defense == null && row.Resistance == 5 && row.AttackSpeed == 120, "live stat fields and invalidated cache");
        Check(s.CombatSeconds == 25 && s.Allies?.Single() is { Name: "阿米娅", Damage: 2500, Healing: 900 }, "game clock, damage and HEAL slot");
        var effect = s.Effects.Single();
        Check(Display.RuleMultipliers([effect, effect], row.Id).Contains("生命 ×1.44") && Display.RuleMultipliers([effect], "enemy_9012_acloon") == "", "known rules, repeated effects and excluded enemy");

        m.Put(dc + LiveReader.Effects, 0);
        var noEffects = reader.Read();
        Check(noEffects.EffectNote != null && noEffects.Effects.Count == 0, "unavailable list distinguished from empty list");
        m.Put(dc + LiveReader.Activity, m.S("other-activity"));
        try { reader.Read(); throw new InvalidDataException("Other activity was accepted"); }
        catch (NoSession) { }
        m.Put(dc + LiveReader.Activity, m.S("act3autochess"));
        m.Put(klass + LiveReader.ClassName, m.C("DifferentController"));
        try { new LiveReader(m, module, catalog).Read(); throw new InvalidDataException("Incompatible battle controller was accepted"); }
        catch (NoSession) { }
        CheckHistory(s, row);
        Console.WriteLine("OK: read-only memory, incompatible layouts and other activities rejected, fixed-point stats, confirmed zero HP, partial samples, wave retention, retained effects, next-match isolation, close/reopen and history file recovery.");
    }

    static void CheckHistory(Snapshot source, EnemyInstance row)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("STRONGHOLD_TEST_ROOT") ?? Path.GetTempPath(), $"stronghold-history-{Guid.NewGuid():N}");
        var file = Path.Combine(dir, "history.json");
        try
        {
            var h = new ObservationHistory(file);
            var t = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.FromHours(8));
            var first = source with { SessionKey = "a", Round = 12, ReadAt = t, Phase = "作战", Enemies = [row, row with { Instance = "other" }] };
            Check(h.Record(first), "first wave boundary");
            var id = h.Current!.Id;
            h.Record(first with { ReadAt = t.AddSeconds(1), Enemies = [row with { Hp = 0, Attack = null }], EnemyListComplete = false, Effects = [], EffectNote = "本帧不完整" });
            var a = h.Current.Waves[12];
            var x = a.Enemies[$"{row.Instance}:{row.Id}"];
            Check(x.Status == "生命归零" && x.Enemy.Hp == 0 && x.Enemy.Attack == row.Attack && x.RetainedAttributes, "zero HP and prior valid attack");
            Check(a.Enemies[$"other:{row.Id}"].Present, "partial frame does not infer exit");
            h.Record(first with { ReadAt = t.AddSeconds(2), Enemies = [], Effects = [], Phase = "结算" });
            Check(a.Enemies.Count == 2 && a.Enemies[$"other:{row.Id}"].Status == "已离场" && a.Enemies[$"other:{row.Id}"].Enemy.Hp == row.Hp, "disappearance retains nonzero last HP");
            Check(a.Effects.Count == 1 && a.EffectsRetained && a.EffectsReadAt == t, "final cleared effects retain last observed time");
            Check(h.Record(first with { Round = 13, ReadAt = t.AddSeconds(3), Enemies = [row with { Hp = 0 }], Effects = [], Phase = "整备" }), "next wave boundary");
            Check(h.Current.Waves[13].Enemies.Count == 0 && h.Current.Waves[13].Effects.Count == 0 && h.Current.Waves[12].Enemies.Count == 2, "new wave starts empty");
            var doubled = first with { Round = 13, ReadAt = t.AddSeconds(3.1), Effects = [source.Effects[0], source.Effects[0]], CombatSeconds = 40 };
            h.Record(doubled);
            h.Record(doubled with { ReadAt = t.AddSeconds(3.2), CombatSeconds = 40.5 });
            h.Record(doubled with { ReadAt = t.AddSeconds(3.5), Phase = "联防", CombatSeconds = 1, Allies = [new("char_002_amiya", "char_002_amiya", "阿米娅", 100, 30)] });
            var wave13 = h.Current.Waves[13];
            Check(wave13.EffectFrames.Count == 2 && wave13.EffectFrames[^1].Effects.Count == 2, "effect history records changes, not repeated sampling");
            Check(wave13.AllySegments.Count == 2 && wave13.AllySegments.Values.Last().Allies.Values.Single().Damage == 100, "clock reset starts separate combat segment");
            h.Record(first with { Round = 14, ReadAt = t.AddSeconds(4), Phase = "领袖战" });
            h.Record(first with { Round = 14, ReadAt = t.AddSeconds(5), Phase = "结束", Enemies = [], Effects = [], EffectNote = "场景已结束" });
            Check(h.Current.SawEnd && h.Current.Waves[14].Effects.Count == 1 && h.Current.Waves[14].Enemies.Count == 2, "end-of-game retention");
            h.Save(force: true);
            Check(h.StorageNote == null && File.Exists(file), "history saves");
            var reloaded = new ObservationHistory(file);
            Check(reloaded.Matches.Single().Id == id && reloaded.Matches.Single().Waves[12].Enemies.Count == 2, "reload older wave");
            reloaded.Record(first with { SessionKey = "b", Round = 1, ReadAt = t.AddSeconds(6) });
            Check(reloaded.Current!.Id != id && reloaded.Matches.Count == 2 && reloaded.Current.Waves.Count == 1, "next game isolated after reopen");
            reloaded.Save(force: true);
            var resumed = new ObservationHistory(file);
            resumed.Record(first with { SessionKey = "b", Round = 2, ReadAt = t.AddSeconds(7) });
            Check(resumed.Matches.Count == 2 && resumed.Current!.Waves.Keys.SequenceEqual(new[] { 1, 2 }), "resume current game after reopening");
            resumed.Save(force: true);
            var c = new ObservationHistory(file);
            for (var i = 0; i < 13; i++) c.Record(first with { SessionKey = $"new-{i}", Round = 1, ReadAt = t.AddMinutes(5 + i) });
            c.Save(force: true);
            Check(new ObservationHistory(file).Matches.Count == ObservationHistory.Limit, "retention cap");
            File.WriteAllText(file, "corrupt test fixture");
            var corrupt = new ObservationHistory(file);
            corrupt.Record(first);
            corrupt.Save(force: true);
            Check(corrupt.StorageNote != null && File.ReadAllText(file) == "corrupt test fixture", "unreadable original history preserved");
        }
        finally
        {
            File.Delete(file);
            if (Directory.Exists(dir)) Directory.Delete(dir);
        }
    }

    static void Check(bool condition, string name) { if (!condition) throw new InvalidDataException(name); }

    sealed class Fixture : IReadMemory
    {
        readonly Dictionary<ulong, byte> _data = new();
        ulong _next = 0x100000;
        public ulong New() { var x = _next; _next += 0x1000; return x; }
        public void Byte(ulong at, byte n) => _data[at] = n;
        public void Put(ulong at, ulong n) { Span<byte> x = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(x, n); Write(at, x); }
        public void Int(ulong at, int n) { Span<byte> x = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(x, n); Write(at, x); }
        public void Float(ulong at, float n) => Int(at, BitConverter.SingleToInt32Bits(n));
        void Write(ulong at, ReadOnlySpan<byte> b) { for (var i = 0; i < b.Length; i++) _data[at + (ulong)i] = b[i]; }
        public byte[] Read(ulong address, int size) => Enumerable.Range(0, size).Select(i => _data.GetValueOrDefault(address + (ulong)i)).ToArray();
        public ulong S(string s) { var a = New(); Int(a + 0x10, s.Length); Write(a + 0x14, Encoding.Unicode.GetBytes(s)); return a; }
        public ulong C(string s) { var a = New(); Write(a, Encoding.ASCII.GetBytes(s + "\0")); return a; }
        public void Class(ulong obj, string n) { var c = New(); Put(obj, c); Put(c + LiveReader.ClassName, C(n)); }
        public ulong Array(params ulong[] items)
        {
            var a = New(); Put(a + LiveReader.ArrayLength, (ulong)items.Length);
            for (var i = 0; i < items.Length; i++) Put(a + LiveReader.ArrayData + (ulong)i * 8, items[i]);
            return a;
        }
        public void Fixed(ulong a, int i, double number)
        {
            var p = a + LiveReader.ArrayData + (ulong)i * LiveReader.ObscuredStride;
            const ulong k = 0x1020304050607080;
            Put(p, k); Put(p + 8, k ^ (ulong)(long)(number * LiveReader.FixedOne)); Byte(p + 0x10, 1);
        }
    }
}
