using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StrongholdObserver;

sealed record EnemyObservation(EnemyInstance Enemy, DateTimeOffset LastSeenAt, bool Present, bool RetainedAttributes)
{
    public string Status => Enemy.Hp == 0 ? "生命归零" : Present ? (RetainedAttributes ? "在场 · 含留存属性" : "在场") : "已离场";
}

sealed record EffectToken(string Id, int Param);
sealed record EffectFrame(DateTimeOffset At, List<EffectToken> Effects);
sealed class AllySegment
{
    public string? Anchor { get; set; }
    public string Phase { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public double? CombatSeconds { get; set; }
    public double? TotalHealing { get; set; }
    public Dictionary<string, AllyPerformance> Allies { get; set; } = new(StringComparer.Ordinal);
}

sealed class WaveRecord
{
    public int Round { get; set; }
    public DateTimeOffset ReadAt { get; set; }
    public string? Phase { get; set; }
    public Dictionary<string, EnemyObservation> Enemies { get; set; } = new(StringComparer.Ordinal);
    public List<CurrentEffect> Effects { get; set; } = [];
    public DateTimeOffset? EffectsReadAt { get; set; }
    public bool EffectsRetained { get; set; }
    public List<EffectFrame> EffectFrames { get; set; } = [];
    public Dictionary<string, AllySegment> AllySegments { get; set; } = new(StringComparer.Ordinal);
    public string? CurrentAllySegment { get; set; }
    public string? AllyNote { get; set; }
    public string? EnemyNote { get; set; }
    public string? EffectNote { get; set; }
}

sealed class MatchRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? SessionKey { get; set; }
    public string Version { get; set; } = "";
    public string ModeId { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool SawEnd { get; set; }
    public int LastRound { get; set; }
    public SortedDictionary<int, WaveRecord> Waves { get; set; } = new();
}

sealed record HistoryDocument(int Format, List<MatchRecord> Matches);
sealed record EnemyExportState(string Instance, string Id, DateTimeOffset LastSeenAt, string Status, bool RetainedAttributes);
sealed record WaveExport(string Version, DateTimeOffset ReadAt, string ModeId, string Mode, int Round, string? Phase,
    Guid MatchId, DateTimeOffset MatchStartedAt, DateTimeOffset? MatchEndedAt, DateTimeOffset? EffectsReadAt,
    bool EffectsRetained, IReadOnlyList<EnemyInstance> Enemies, IReadOnlyList<CurrentEffect> Effects, IReadOnlyList<EnemyExportState> EnemyStates,
    IReadOnlyList<EffectFrame> EffectFrames, IReadOnlyList<AllySegment> AllySegments);

sealed class ObservationHistory
{
    public const int Limit = 12;
    readonly string _file;
    readonly string _lockName;
    readonly List<MatchRecord> _matches;
    bool _dirty;
    DateTimeOffset _lastWrite;
    public MatchRecord? Current { get; private set; }
    public IReadOnlyList<MatchRecord> Matches => _matches;
    public string? StorageNote { get; private set; }

    public ObservationHistory(string file)
    {
        _file = Path.GetFullPath(file);
        _lockName = @"Local\StrongholdHistory-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_file.ToUpperInvariant())))[..24];
        try { _matches = Load(_file); }
        catch (Exception e) when (IsStorageFault(e)) { _matches = []; StorageNote = "历史文件暂不可读；原文件已保留。"; }
    }

    public bool Record(Snapshot s)
    {
        var same = Current ?? _matches.FirstOrDefault(x => !x.SawEnd && x.EndedAt == null && s.SessionKey != null && x.SessionKey == s.SessionKey && x.ModeId == s.ModeId);
        var roundRestart = same != null && (s.Round < same.LastRound || (same.SawEnd && s.Round == 1 && s.Phase is "加载" or "整备" or "作战"));
        var isNew = same == null || same.SessionKey != s.SessionKey || same.ModeId != s.ModeId || roundRestart;
        if (isNew)
        {
            if (Current is { } previous)
            {
                CloseWave(previous);
                previous.EndedAt ??= previous.LastSeenAt;
            }
            same = new MatchRecord { SessionKey = s.SessionKey, Version = s.Version, ModeId = s.ModeId, Mode = s.Mode, StartedAt = s.ReadAt };
            _matches.Insert(0, same);
            if (_matches.Count > Limit) _matches.RemoveRange(Limit, _matches.Count - Limit);
        }
        Current = same!;
        var match = Current;
        var changedRound = !match.Waves.TryGetValue(s.Round, out var wave);
        if (changedRound)
        {
            CloseWave(match);
            wave = new WaveRecord { Round = s.Round };
            match.Waves.Add(s.Round, wave);
        }
        match.LastRound = s.Round;
        match.LastSeenAt = s.ReadAt;
        UpdateWave(wave!, s);

        var ended = !match.SawEnd && s.Phase == "结束";
        if (ended) { match.SawEnd = true; match.EndedAt = s.ReadAt; }
        if (match.SawEnd) CloseWave(match);
        _dirty = true;
        return isNew || changedRound || ended;
    }

    static void CloseWave(MatchRecord match)
    {
        if (!match.Waves.TryGetValue(match.LastRound, out var wave)) return;
        foreach (var (key, old) in wave.Enemies.ToArray())
            if (old.Present) wave.Enemies[key] = old with { Present = false };
    }

    static void UpdateWave(WaveRecord wave, Snapshot s)
    {
        wave.ReadAt = s.ReadAt;
        wave.Phase = s.Phase;
        wave.EnemyNote = s.EnemyNote;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var canAdd = s.Phase == null || s.Phase is "作战" or "联防" or "领袖战" or "暂停";
        foreach (var enemy in s.Enemies)
        {
            var key = $"{enemy.Instance}:{enemy.Id}";
            seen.Add(key);
            var old = wave.Enemies.GetValueOrDefault(key)?.Enemy;
            if (old == null && !canAdd) continue; // A transition may still expose instances from the previous wave.
            if (old == null && wave.Enemies.Count >= 4096) { wave.EnemyNote = "已达到单波记录上限（4,096 个实例）。"; continue; }
            var retained = old != null && (enemy.Hp == null && old.Hp != null || enemy.MaxHp == null && old.MaxHp != null ||
                enemy.Attack == null && old.Attack != null || enemy.Defense == null && old.Defense != null ||
                enemy.Resistance == null && old.Resistance != null || enemy.AttackSpeed == null && old.AttackSpeed != null);
            var row = enemy with { Hp = enemy.Hp ?? old?.Hp, MaxHp = enemy.MaxHp ?? old?.MaxHp, Attack = enemy.Attack ?? old?.Attack,
                Defense = enemy.Defense ?? old?.Defense, Resistance = enemy.Resistance ?? old?.Resistance, AttackSpeed = enemy.AttackSpeed ?? old?.AttackSpeed };
            wave.Enemies[key] = new(row, s.ReadAt, enemy.Hp != 0, retained);
        }
        if (s.EnemyListComplete)
            foreach (var (key, old) in wave.Enemies.ToArray())
                if (old.Present && !seen.Contains(key)) wave.Enemies[key] = old with { Present = false };

        wave.EffectNote = s.EffectNote;
        if (s.EffectNote == null && (s.Effects.Count > 0 || wave.Effects.Count == 0 || s.Phase is "作战" or "联防" or "领袖战"))
        {
            wave.Effects = s.Effects.ToList();
            wave.EffectsReadAt = s.ReadAt;
            wave.EffectsRetained = false;
            var frame = s.Effects.Select(x => new EffectToken(x.Id, x.Param)).OrderBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Param).ToList();
            if (wave.EffectFrames.Count == 0 || !wave.EffectFrames[^1].Effects.SequenceEqual(frame))
            {
                wave.EffectFrames.Add(new EffectFrame(s.ReadAt, frame));
                if (wave.EffectFrames.Count > 128) wave.EffectFrames.RemoveAt(1);
            }
        }
        else wave.EffectsRetained = wave.EffectsReadAt != null;

        wave.AllyNote = s.AllyNote;
        if (s.Allies is { } observed && s.CombatSegment is { } anchor)
        {
            var segment = wave.CurrentAllySegment is { } key ? wave.AllySegments.GetValueOrDefault(key) : null;
            var reset = segment != null && s.CombatSeconds is { } now && segment.CombatSeconds is { } prev && now + .25 < prev;
            if (!reset && segment != null && s.Phase is not (null or "暂停") && segment.Phase != s.Phase && s.CombatSeconds is <= 1.25)
                reset = observed.Any(a => segment.Allies.TryGetValue($"{a.Instance}:{a.Id}", out var old) && a.Damage < old.Damage);
            if (segment == null || segment.Anchor != anchor || reset)
            {
                if (wave.AllySegments.Count >= 16) return;
                var segmentKey = $"段{wave.AllySegments.Count + 1}";
                segment = new AllySegment { Phase = s.Phase is null or "暂停" ? "作战" : s.Phase, Anchor = anchor };
                wave.AllySegments.Add(segmentKey, segment);
                wave.CurrentAllySegment = segmentKey;
            }
            segment.At = s.ReadAt;
            segment.CombatSeconds = MaxKnown(segment.CombatSeconds, s.CombatSeconds);
            segment.TotalHealing = MaxKnown(segment.TotalHealing, s.TotalHealing);
            foreach (var ally in observed)
            {
                var k = $"{ally.Instance}:{ally.Id}";
                if (segment.Allies.TryGetValue(k, out var old))
                    segment.Allies[k] = ally with { Damage = MaxKnown(old.Damage, ally.Damage), Healing = MaxKnown(old.Healing, ally.Healing) };
                else if (segment.Allies.Count < 512) segment.Allies[k] = ally;
            }
        }
    }

    static double? MaxKnown(double? a, double? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);

    public void Save(bool force = false)
    {
        if (!_dirty || !force && DateTimeOffset.UtcNow - _lastWrite < TimeSpan.FromSeconds(4)) return;
        _lastWrite = DateTimeOffset.UtcNow;
        var temp = _file + $".{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            using var mutex = new Mutex(false, _lockName);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(250); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { StorageNote = "历史文件正在使用，稍后重试。"; return; }
                var all = Load(_file).ToDictionary(m => m.Id);
                foreach (var m in _matches)
                {
                    if (!all.TryGetValue(m.Id, out var disk) || m.LastSeenAt >= disk.LastSeenAt) all[m.Id] = m;
                }
                var doc = new HistoryDocument(1, all.Values.OrderByDescending(x => x.StartedAt).Take(Limit).ToList());
                using (var stream = File.Create(temp)) JsonSerializer.Serialize(stream, doc, JsonOptions.Options);
                File.Move(temp, _file, true);
                _dirty = false;
                StorageNote = null;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception ex) when (IsStorageFault(ex)) { StorageNote = "历史暂未写入；原文件已保留。"; }
        finally { try { File.Delete(temp); } catch (Exception ex) when (IsStorageFault(ex)) { } }
    }

    static List<MatchRecord> Load(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<HistoryDocument>(stream, JsonOptions.Options);
        if (doc is not { Format: 1, Matches: not null } || doc.Matches.Any(m => m == null || m.Waves == null ||
                m.Waves.Any(w => w.Key is < 1 or > 30 || w.Value == null || w.Value.Enemies == null || w.Value.Effects == null)) ||
            doc.Matches.Select(m => m.Id).Distinct().Count() != doc.Matches.Count) throw new InvalidDataException("历史文件格式无效。");
        return doc.Matches.OrderByDescending(m => m.StartedAt).Take(Limit).ToList();
    }

    static bool IsStorageFault(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.Security.SecurityException;
}
