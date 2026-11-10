using System.Diagnostics;
using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace StrongholdObserver;

sealed class MainForm : Form
{
    static readonly Color Background = Color.FromArgb(15, 21, 24), Surface = Color.FromArgb(23, 31, 35), Raised = Color.FromArgb(31, 43, 47),
        Edge = Color.FromArgb(48, 63, 67), TextColor = Color.FromArgb(237, 241, 240), Muted = Color.FromArgb(166, 180, 180), Accent = Color.FromArgb(132, 231, 194), Warning = Color.FromArgb(255, 199, 125);
    readonly Catalog _catalog;
    readonly ObserverSession _session;
    readonly ObservationHistory _history;
    readonly ColumnWidths _columnWidths;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 550 };
    readonly System.Windows.Forms.Timer _widthSaveTimer = new() { Interval = 350 };
    readonly Label _status = new(), _summary = new(), _effectDetail = new(), _foot = new(), _enemyInfo = new(), _allyInfo = new();
    readonly TableGrid _enemies = new(), _effects = new(), _allies = new();
    readonly ComboBox _matchChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 305 };
    readonly ComboBox _waveChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125, Enabled = false };
    readonly Button _connect, _admin, _save;
    MatchRecord? _viewMatch;
    WaveRecord? _viewWave;
    bool _reading, _enabled, _closing, _changingPickers, _lastReadOk;

    public MainForm(Catalog catalog, bool auto, ObservationHistory? history = null, ColumnWidths? columnWidths = null)
    {
        _catalog = catalog;
        _session = new ObserverSession(catalog);
        _history = history ?? new(Path.Combine(AppContext.BaseDirectory, "记录", "history.json"));
        _columnWidths = columnWidths ?? new(Path.Combine(AppContext.BaseDirectory, "记录", "列宽.json"));
        Text = $"卫戍协议 · 实时观察器 {Application.ProductVersion.Split('+')[0]}";
        BackColor = Background;
        ForeColor = TextColor;
        Font = new Font("Microsoft YaHei UI", 10F);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1040, 650);
        MinimumSize = new Size(790, 550);
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Insets(16, 14, 16, 10), ColumnCount = 1, RowCount = 6 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Insets(0, 0, 0, 10), RowCount = 2, ColumnCount = 2 };
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var series = Label("ARKNIGHTS   /   STRONGHOLD PROTOCOL   /   V3_1", Accent, 10F, FontStyle.Bold);
        series.Margin = Insets(0, 0, 0, 4);
        top.Controls.Add(series, 0, 0);
        var title = Label("卫戍协议 · 实时观察器", TextColor, 24F, FontStyle.Bold);
        title.Margin = Padding.Empty;
        top.Controls.Add(title, 0, 1);
        var badge = Label("独立只读工具", Accent, 10F, FontStyle.Bold);
        badge.Anchor = AnchorStyles.Right;
        badge.Margin = Insets(12, 0, 0, 0);
        top.Controls.Add(badge, 1, 1);
        root.Controls.Add(top);

        var buttons = FlowRow(10);
        _connect = Button("连接游戏", primary: true);
        _connect.Click += (_, _) => SetConnection(!_enabled);
        _admin = Button("以管理员权限重开");
        _admin.Enabled = !ProcessMemory.Elevated;
        _admin.Click += (_, _) => ReopenElevated();
        _save = Button("导出所选波次");
        _save.Enabled = false;
        _save.Click += (_, _) => Save();
        buttons.Controls.AddRange([_connect, _admin, _save]);
        root.Controls.Add(buttons);

        var strip = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, BackColor = Surface, Padding = Insets(12, 5, 12, 5),
            Margin = Insets(0, 0, 0, 10), MinimumSize = new Size(0, LogicalToDeviceUnits(36)) };
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        StyleLabel(_status, Muted);
        _status.Text = "未连接 · 请打开 PC 客户端并进入卫戍协议";
        StyleLabel(_summary, Accent);
        _summary.TextAlign = ContentAlignment.MiddleRight;
        strip.Controls.Add(_status, 0, 0);
        strip.Controls.Add(_summary, 1, 0);
        root.Controls.Add(strip);

        var picks = FlowRow(12);
        // The native ComboBox can be a little taller than the flow layout's preferred size.
        picks.MinimumSize = new Size(0, LogicalToDeviceUnits(28));
        var matchLabel = Label("对局", Muted, 10F, FontStyle.Regular);
        var waveLabel = Label("波次", Muted, 10F, FontStyle.Regular);
        matchLabel.Margin = Insets(0, 0, 8, 0);
        waveLabel.Margin = Insets(16, 0, 8, 0);
        matchLabel.Anchor = waveLabel.Anchor = _matchChoice.Anchor = _waveChoice.Anchor = AnchorStyles.Left;
        _matchChoice.Margin = _waveChoice.Margin = Padding.Empty;
        _matchChoice.AccessibleName = "选择实时或历史对局";
        _waveChoice.AccessibleName = "选择历史波次";
        picks.Controls.AddRange([matchLabel, _matchChoice, waveLabel, _waveChoice]);
        root.Controls.Add(picks);
        _matchChoice.SelectedIndexChanged += (_, _) => { if (!_changingPickers) { UpdateWavePicker(reset: true); RenderView(); } };
        _waveChoice.SelectedIndexChanged += (_, _) => { if (!_changingPickers) RenderView(); };

        var pages = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6), Margin = Padding.Empty };
        var pageInset = Insets(8, 10, 8, 8);
        var enemies = new TabPage("本波敌人") { BackColor = Surface, Padding = pageInset };
        var effects = new TabPage("回合强化") { BackColor = Surface, Padding = pageInset };
        var allies = new TabPage("我方战报") { BackColor = Surface, Padding = pageInset };
        pages.TabPages.AddRange([enemies, effects, allies]);
        root.Controls.Add(pages);
        SetupGrid(_enemies, ("name", "敌人"), ("state", "状态"), ("hp", "最后生命"), ("maxHp", "最大生命"), ("attack", "攻击"), ("defense", "防御"), ("res", "法抗"), ("aspd", "攻速"), ("id", "敌人 ID"));
        SetupGrid(_effects, ("name", "效果"), ("summary", "每份规则"), ("copies", "末次份数"), ("peak", "本波峰值"), ("rounds", "已见波数"), ("param", "原始参数"), ("id", "效果 ID"));
        SetupGrid(_allies, ("name", "干员"), ("phase", "阶段"), ("damage", "累计输出"), ("dps", "阶段平均 DPS"), ("heal", "个人治疗"), ("seconds", "战斗秒数"), ("id", "干员 ID"));
        AddGridPanel(enemies, _enemies, _enemyInfo);
        AddGridPanel(effects, _effects, _effectDetail);
        AddGridPanel(allies, _allies, _allyInfo);
        _enemies.ConfigureWidths(_columnWidths, "enemies", QueueColumnSave);
        _effects.ConfigureWidths(_columnWidths, "effects", QueueColumnSave);
        _allies.ConfigureWidths(_columnWidths, "allies", QueueColumnSave, nameWidth: 168);
        _enemyInfo.Text = "数值只来自已生成的敌人实例；尚未生成的敌人不会出现在本表。";
        _effectDetail.Text = "选择一项强化可查看其配置说明。此页读取卫戍当前回合的全局效果。";
        _effects.SelectionChanged += (_, _) => UpdateEffectDetail();
        _enemies.SelectionChanged += (_, _) => { UpdateEnemyDetail(); UpdateEffectDetail(); };
        StyleLabel(_foot, Muted);
        _foot.MinimumSize = new Size(0, LogicalToDeviceUnits(22));
        _foot.Margin = Insets(0, 6, 0, 0);
        _foot.Text = "自动保存最近 12 局 · 仅保存在工具的「记录」文件夹";
        root.Controls.Add(_foot);

        _timer.Tick += async (_, _) => await PollAsync();
        _widthSaveTimer.Tick += (_, _) => SaveColumnWidths();
        FormClosed += (_, _) => { _closing = true; _timer.Stop(); SaveColumnWidths(); _history.Save(force: true); _session.Dispose(); };
        Disposed += (_, _) => _widthSaveTimer.Dispose();
        RefreshPickers();
        RenderView();
        if (auto) Shown += (_, _) => SetConnection(true);
    }

    void QueueColumnSave()
    {
        _widthSaveTimer.Stop();
        _widthSaveTimer.Start();
    }

    void SaveColumnWidths()
    {
        _widthSaveTimer.Stop();
        if (!_columnWidths.Save()) _foot.Text = "列宽暂未保存，请确认程序目录可写。";
    }

    protected override void OnLoad(EventArgs e)
    {
        var workArea = Screen.FromControl(this).WorkingArea;
        var edge = LogicalToDeviceUnits(24);
        var availableWidth = workArea.Width - (Width - ClientSize.Width) - edge;
        var availableHeight = workArea.Height - (Height - ClientSize.Height) - edge;
        MinimumSize = new Size(Math.Min(workArea.Width, Math.Max(MinimumSize.Width, LogicalToDeviceUnits(520))),
            Math.Min(workArea.Height, Math.Max(MinimumSize.Height, LogicalToDeviceUnits(430))));
        ClientSize = new Size(Math.Min(LogicalToDeviceUnits(1280), Math.Max(availableWidth, 1)),
            Math.Min(LogicalToDeviceUnits(650), Math.Max(availableHeight, 1)));
        if (StartPosition == FormStartPosition.CenterScreen)
        {
            Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
            Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
        }
        base.OnLoad(e);
    }

    void SetConnection(bool enabled)
    {
        _enabled = enabled;
        _connect.Text = enabled ? "暂停读取" : "连接游戏";
        if (enabled) { _status.Text = "正在连接…"; _timer.Start(); _ = PollAsync(); }
        else { _timer.Stop(); _lastReadOk = false; _history.Save(force: true); _status.Text = "已暂停 · 记录仍可回看"; RenderView(); }
    }

    async Task PollAsync()
    {
        if (!_enabled || _reading || _closing) return;
        _reading = true;
        try
        {
            var sample = await Task.Run(_session.Sample);
            if (_closing || !_enabled) return;
            _lastReadOk = true;
            var boundary = _history.Record(sample);
            _history.Save(boundary);
            _status.ForeColor = Accent;
            _status.Text = sample.Phase == "结束" ? "对局已结束 · 本波数据已留存" : sample.EnemyNote == null && sample.EffectNote == null ? "已连接 · 正在读取当前对局" : "已连接 · 部分数据待游戏生成";
            if (boundary || _matchChoice.Items.Count != _history.Matches.Count + 1) RefreshPickers();
            RenderView();
        }
        catch (Exception ex) when (ex is NoSession or ReadFault or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (_closing || !_enabled) return;
            _status.ForeColor = Warning;
            _status.Text = ex.Message;
            _lastReadOk = false;
            _history.Save();
            RenderView();
            if (ex.Message.Contains("错误 5", StringComparison.Ordinal))
            {
                _enabled = false;
                _timer.Stop();
                _connect.Text = "连接游戏";
            }
        }
        finally { _reading = false; }
    }

    void ReopenElevated()
    {
        try
        {
            SaveColumnWidths();
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("找不到观察器启动文件。");
            if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(AppContext.BaseDirectory, "StrongholdObserver.exe");
            Process.Start(new ProcessStartInfo(path, "--connect") { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
            Close();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _status.ForeColor = Warning;
            _status.Text = "没有启动管理员实例。";
        }
    }

    void Save()
    {
        if (_viewMatch is not { } m || _viewWave is not { } w) return;
        var exported = new WaveExport(m.Version, w.ReadAt, m.ModeId, m.Mode, w.Round, w.Phase, m.Id, m.StartedAt, m.EndedAt,
            w.EffectsReadAt, w.EffectsRetained, w.Enemies.Values.Select(x => x.Enemy).ToArray(), w.Effects.ToArray(),
            w.Enemies.Values.Select(x => new EnemyExportState(x.Enemy.Instance, x.Enemy.Id, x.LastSeenAt, x.Status, x.RetainedAttributes)).ToArray(),
            w.EffectFrames.ToArray(), w.AllySegments.Values.ToArray());
        using var save = new SaveFileDialog { Filter = "JSON 快照 (*.json)|*.json", AddExtension = true, FileName = $"卫戍记录-第{w.Round}回合-{m.StartedAt:yyyyMMdd-HHmmss}.json" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(save.FileName, JsonSerializer.Serialize(exported, JsonOptions.Options)); _foot.Text = $"已导出：{save.FileName}"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _foot.Text = "保存失败：" + e.Message; }
    }

    void UpdateEffectDetail()
    {
        if (_viewWave is not { } wave || _viewMatch is not { } match) { _effectDetail.Text = "还没有回合记录。"; return; }
        var meta = _catalog.ModeInfo?.GetValueOrDefault(match.ModeId);
        var intensity = meta != null ? $"{match.Mode} · 模拟强度 {meta.Intensity ?? "—"} · 卫戍认证 ×{meta.CreditFactor:0.##}。强度标记与认证倍率不作为额外敌人加成。" : match.Mode;
        var prefix = intensity + $"\n本波末次列表：{wave.Effects.Count} 份、{wave.Effects.Select(e => e.Id).Distinct().Count()} 种。";
        prefix += wave.EffectFrames.Count > 0 ? $" 本波有 {wave.EffectFrames.Count} 次不同于前一次的列表记录。" : " 旧版只保留了末次列表。";
        if (wave.EffectsRetained) prefix += $" 留存于 {wave.EffectsReadAt:HH:mm:ss}。";
        if (SelectedEnemy() is { } enemy)
        {
            var calc = Display.RuleMultipliers(wave.Effects, enemy.Id);
            if (calc.Length > 0) prefix += $"\n{enemy.Name} · 名单规则每份算一次（含排除项检查）：{calc}。";
        }
        if (_effects.CurrentRow?.Tag is not string id)
        {
            _effectDetail.Text = prefix + "\n" + (wave.Effects.Count == 0 ? (wave.EffectNote ?? (wave.EffectsReadAt != null ? "已读取：该波未报告强化。" : "该波没有强化读取记录。")) : "选择一项强化可查看说明。");
            return;
        }
        var metaEffect = _catalog.Effects.GetValueOrDefault(id);
        var latest = wave.Effects.Count(x => x.Id == id);
        var frames = wave.EffectFrames.Where(f => f.Effects.Any(x => x.Id == id)).ToArray();
        var peak = Math.Max(latest, frames.Select(f => f.Effects.Count(x => x.Id == id)).DefaultIfEmpty().Max());
        var shape = peak > 1 ? $"观察到同一帧最多 {peak} 份" : "记录中每帧最多一份";
        var seen = frames.Length > 0 ? $"；本波首次 {frames[0].At:HH:mm:ss}" : "";
        var prior = match.Waves.Values.LastOrDefault(w => w.Round < wave.Round && w.EffectsReadAt != null);
        var change = prior == null ? "" : $"；第 {prior.Round} 波末次 {prior.Effects.Count(x => x.Id == id)} 份 → 本波末次 {latest} 份";
        _effectDetail.Text = prefix + $"\n{metaEffect?.Name ?? id}：{shape}{seen}{change}。参数不是层数或百分比。\n{metaEffect?.Desc}";
    }

    EnemyInstance? SelectedEnemy() => _enemies.CurrentRow?.Tag is string key && _viewWave != null && _viewWave.Enemies.TryGetValue(key, out var item) ? item.Enemy : null;

    void UpdateEnemyDetail()
    {
        if (_viewWave is not { } wave) return;
        var live = wave.Enemies.Values.Count(x => x.Present);
        var died = wave.Enemies.Values.Count(x => x.Enemy.Hp == 0);
        var count = _lastReadOk && _viewMatch == _history.Current && wave.Round == _viewMatch?.LastRound && !(_viewMatch?.SawEnd ?? false) ? $"在场 {live} · " : "";
        _enemyInfo.Text = $"{count}本波已记录 {wave.Enemies.Count} 个实例 · 观测到归零 {died} · {wave.ReadAt:HH:mm:ss}";
        if (SelectedEnemy() is { } enemy && _catalog.EnemyBase?.GetValueOrDefault(enemy.Id) is { } b)
            _enemyInfo.Text += $"\n{enemy.Name}｜实测相对赛季基准：{Display.Measured(enemy, b)}";
        else if (wave.EnemyNote != null) _enemyInfo.Text += "\n" + wave.EnemyNote;
    }

    void RefreshPickers()
    {
        var match = (_matchChoice.SelectedItem as MatchChoice)?.Id;
        _changingPickers = true;
        _matchChoice.BeginUpdate();
        try
        {
            _matchChoice.Items.Clear();
            _matchChoice.Items.Add(new MatchChoice(null, "实时 / 最近一次读取"));
            foreach (var m in _history.Matches) _matchChoice.Items.Add(new MatchChoice(m.Id, $"{m.StartedAt:MM-dd HH:mm:ss} · {m.Mode} · {m.LastRound} 波"));
            var i = _matchChoice.Items.Cast<MatchChoice>().ToList().FindIndex(x => x.Id == match);
            _matchChoice.SelectedIndex = Math.Max(0, i);
        }
        finally { _matchChoice.EndUpdate(); _changingPickers = false; }
        UpdateWavePicker(reset: false);
    }

    MatchRecord? PickMatch()
    {
        var id = (_matchChoice.SelectedItem as MatchChoice)?.Id;
        return id is { } value ? _history.Matches.FirstOrDefault(m => m.Id == value) : _history.Current ?? _history.Matches.FirstOrDefault();
    }

    void UpdateWavePicker(bool reset)
    {
        var match = PickMatch();
        var historical = (_matchChoice.SelectedItem as MatchChoice)?.Id != null;
        var round = !reset ? (_waveChoice.SelectedItem as WaveChoice)?.Round : null;
        _changingPickers = true;
        _waveChoice.BeginUpdate();
        try
        {
            _waveChoice.Items.Clear();
            if (match != null)
            {
                foreach (var wave in match.Waves.Values) _waveChoice.Items.Add(new WaveChoice(wave.Round));
                var choice = historical ? round ?? match.LastRound : match.LastRound;
                var i = _waveChoice.Items.Cast<WaveChoice>().ToList().FindIndex(x => x.Round == choice);
                _waveChoice.SelectedIndex = i < 0 ? _waveChoice.Items.Count - 1 : i;
            }
            _waveChoice.Enabled = historical && _waveChoice.Items.Count > 0;
        }
        finally { _waveChoice.EndUpdate(); _changingPickers = false; }
    }

    void RenderView()
    {
        var match = PickMatch();
        var historical = (_matchChoice.SelectedItem as MatchChoice)?.Id != null;
        var round = historical ? (_waveChoice.SelectedItem as WaveChoice)?.Round : match?.LastRound;
        var wave = round is { } r ? match?.Waves.GetValueOrDefault(r) : null;
        var changedWave = !ReferenceEquals(_viewWave, wave);
        _viewMatch = match;
        _viewWave = wave;
        _save.Enabled = wave != null;
        if (_history.StorageNote is { } storage) _foot.Text = storage;
        if (match == null || wave == null) { _summary.Text = "暂无记录"; return; }

        var mode = historical ? "历史" : _lastReadOk && !match.SawEnd ? "实时" : "留存";
        _summary.Text = $"{mode} · {match.Mode} · 第 {wave.Round} 波" + (wave.Phase == null ? "" : $" · {wave.Phase}");
        ReplaceRows(_enemies, changedWave, wave.Enemies.Select(kv =>
        {
            var x = kv.Value; var e = x.Enemy;
            var state = x.Enemy.Hp == 0 ? x.Status : mode == "历史" || !_lastReadOk ? (x.Present ? "最后记录" : x.Status) : x.Status;
            return (kv.Key, new object?[] { e.Name, state, e.Hp, e.MaxHp, e.Attack, e.Defense, e.Resistance, e.AttackSpeed, e.Id });
        }));
        var effectIds = wave.Effects.Select(x => x.Id).Concat(wave.EffectFrames.SelectMany(f => f.Effects.Select(x => x.Id))).Distinct().ToArray();
        ReplaceRows(_effects, changedWave, effectIds.Select(id =>
        {
            var current = wave.Effects.Where(e => e.Id == id).Select(e => e.Param).ToArray();
            var latestNonEmpty = wave.EffectFrames.LastOrDefault(f => f.Effects.Any(e => e.Id == id));
            var parameters = current.Length > 0 ? current : latestNonEmpty?.Effects.Where(e => e.Id == id).Select(e => e.Param).ToArray() ?? [];
            var peak = Math.Max(current.Length, wave.EffectFrames.Select(f => f.Effects.Count(e => e.Id == id)).DefaultIfEmpty().Max());
            var rounds = match.Waves.Values.Count(w => w.Round <= wave.Round && (w.Effects.Any(e => e.Id == id) || w.EffectFrames.Any(f => f.Effects.Any(e => e.Id == id))));
            var info = _catalog.Effects.GetValueOrDefault(id);
            return (id, new object?[] { info?.Name ?? id, info == null ? null : Display.Describe(info.Rules), current.Length, peak, rounds, string.Join(", ", parameters), id });
        }));
        ReplaceRows(_allies, changedWave, wave.AllySegments.SelectMany(seg => seg.Value.Allies.Select(kv =>
        {
            var a = kv.Value;
            var s = seg.Value.CombatSeconds;
            return ($"{seg.Key}:{kv.Key}", new object?[] { a.Name, $"{seg.Value.Phase} · {seg.Key}", a.Damage, s > 0 ? a.Damage / s : null, a.Healing, s, a.Id });
        })));
        _allyInfo.Text = wave.AllySegments.Count == 0 ? wave.AllyNote ?? "这波还没有干员战报（早期版本的历史不含此数据）。" :
            "阶段平均 DPS = 该段累计输出 ÷ 游戏计时。同一干员 ID 按游戏战报合并。\n治疗是游戏记账量，不保证排除溢出。" + (wave.AllyNote == null ? "" : " " + wave.AllyNote);
        UpdateEnemyDetail();
        UpdateEffectDetail();
    }

    sealed record MatchChoice(Guid? Id, string Text) { public override string ToString() => Text; }
    sealed record WaveChoice(int Round) { public override string ToString() => $"第 {Round} 波"; }

    Padding Insets(int left, int top, int right, int bottom) => new(
        LogicalToDeviceUnits(left), LogicalToDeviceUnits(top), LogicalToDeviceUnits(right), LogicalToDeviceUnits(bottom));

    FlowLayoutPanel FlowRow(int bottomGap) => new()
    {
        Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false, Margin = Insets(0, 0, 0, bottomGap),
    };

    Button Button(string text, bool primary = false)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat,
            ForeColor = primary ? Background : TextColor, BackColor = primary ? Accent : Raised, Padding = Insets(12, 3, 12, 3),
            Margin = Insets(0, 0, 9, 0), MinimumSize = new Size(0, LogicalToDeviceUnits(34)) };
        b.FlatAppearance.BorderColor = primary ? Accent : Edge;
        return b;
    }

    static Label Label(string text, Color color, float size, FontStyle style) => new() { Text = text, AutoSize = true, ForeColor = color, Font = new Font("Microsoft YaHei UI", size, style) };
    static void StyleLabel(Label label, Color color) { label.Dock = DockStyle.Fill; label.ForeColor = color; label.TextAlign = ContentAlignment.MiddleLeft; label.AutoEllipsis = true; }
    void AddGridPanel(TabPage tab, TableGrid grid, Label detail)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var detailRow = new RowStyle(SizeType.Absolute, 0);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(detailRow);
        layout.Controls.Add(grid, 0, 0);
        var pane = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, AutoScroll = true, AccessibleName = $"{tab.Text}详情" };
        detail.ForeColor = Muted;
        detail.UseMnemonic = false;
        pane.Controls.Add(detail);
        layout.Controls.Add(pane, 0, 1);
        tab.Controls.Add(layout);

        bool fitting = false;
        void FitDetail()
        {
            if (fitting || layout.ClientSize.Width == 0 || layout.ClientSize.Height == 0) return;
            fitting = true;
            try
            {
                var inset = Insets(3, 8, 3, 8);
                // Let the description grow without taking over the table in a short window.
                var limit = Math.Max(1, layout.ClientSize.Height * 2 / 5);
                var width = Math.Max(1, layout.ClientSize.Width - inset.Horizontal);
                int Measure(int w) => detail.GetPreferredSize(new Size(w, 0)).Height;
                var height = detail.Text.Length == 0 ? 0 : Measure(width);
                if (height > 0 && height + inset.Vertical > limit)
                {
                    width = Math.Max(1, width - SystemInformation.VerticalScrollBarWidth);
                    height = Measure(width);
                }
                var contentHeight = height == 0 ? 0 : height + inset.Vertical;
                var viewport = Math.Min(contentHeight, limit);
                if (detailRow.Height == viewport && detail.Size == new Size(width, height)
                    && pane.AutoScrollMinSize.Height == contentHeight) return;

                var scroll = -pane.AutoScrollPosition.Y;
                pane.AutoScrollPosition = Point.Empty;
                detail.Bounds = new Rectangle(inset.Left, inset.Top, width, height);
                pane.AutoScrollMinSize = new Size(0, contentHeight);
                detailRow.Height = viewport;
                pane.AutoScrollPosition = new Point(0, scroll);
            }
            finally { fitting = false; }
        }

        detail.TextChanged += (_, _) => FitDetail();
        detail.FontChanged += (_, _) => FitDetail();
        layout.ClientSizeChanged += (_, _) => FitDetail();
        FitDetail();
    }

    static void SetupGrid(TableGrid grid, params (string Id, string Title)[] cols)
    {
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Surface;
        grid.GridColor = Edge;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Raised, ForeColor = TextColor, SelectionBackColor = Raised, SelectionForeColor = TextColor, WrapMode = DataGridViewTriState.False };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = TextColor, SelectionBackColor = Color.FromArgb(41, 72, 68), SelectionForeColor = TextColor, NullValue = "—", WrapMode = DataGridViewTriState.False };
        grid.ScrollBars = ScrollBars.Both;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(27, 36, 40);
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.ColumnHeadersHeight = 32;
        grid.RowTemplate.Height = 31;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.ReadOnly = true;
        foreach (var c in cols)
        {
            var numeric = TableGrid.IsNumeric(c.Id);
            var column = new DataGridViewTextBoxColumn { Name = c.Id, HeaderText = c.Title, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.Programmatic };
            if (numeric) { column.DefaultCellStyle.Format = "#,0.##"; column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; }
            grid.Columns.Add(column);
        }
    }

    static void ReplaceRows(TableGrid grid, bool resetWidths, IEnumerable<(string Key, object?[] Cells)> items)
    {
        if (resetWidths) grid.MeasureHeaders();
        var data = items.ToArray();
        var wanted = data.Select(x => x.Key).ToHashSet();
        var resort = false;
        for (var i = grid.Rows.Count - 1; i >= 0; i--)
            if (grid.Rows[i].Tag is not string key || !wanted.Contains(key)) { grid.Rows.RemoveAt(i); resort = true; }
        var old = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is string).ToDictionary(r => (string)r.Tag!);
        foreach (var (key, cells) in data)
        {
            if (!old.TryGetValue(key, out var row))
            {
                row = grid.Rows[grid.Rows.Add(cells)];
                row.Tag = key;
                foreach (DataGridViewCell cell in row.Cells) grid.MeasureCell(cell);
                resort = true;
                continue;
            }
            for (var i = 0; i < cells.Length; i++)
            {
                var cell = row.Cells[i];
                var changed = !Equals(cell.Value, cells[i]);
                if (changed)
                {
                    cell.Value = cells[i];
                    if (i == grid.SortIndex) resort = true;
                }
                if (changed || resetWidths) grid.MeasureCell(cell);
            }
        }
        grid.ApplyWidths();
        if (resort) grid.ApplySort();
    }

    sealed class TableGrid : DataGridView
    {
        public int SortIndex { get; private set; } = -1;
        bool _descending;
        bool _applyingWidths;
        ColumnWidths? _savedWidths;
        Action? _widthChanged;
        string[] _widthKeys = [];
        int[] _naturalWidths = [];
        int _nameWidth, _extraWidth;
        public TableGrid() => DoubleBuffered = true;

        public void ConfigureWidths(ColumnWidths saved, string table, Action changed, int nameWidth = 0)
        {
            _savedWidths = saved;
            _widthChanged = changed;
            _nameWidth = nameWidth;
            _widthKeys = Columns.Cast<DataGridViewColumn>().Select(c => $"{table}:{c.Name}").ToArray();
            _naturalWidths = new int[Columns.Count];
            MeasureHeaders();
            ApplyWidths();
        }

        public void MeasureHeaders()
        {
            _extraWidth = TextRenderer.MeasureText("宽宽宽", Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            for (var i = 0; i < Columns.Count; i++)
            {
                var width = Columns[i].GetPreferredWidth(DataGridViewAutoSizeColumnMode.ColumnHeader, fixedHeight: true) + _extraWidth;
                _naturalWidths[i] = i == 0 ? Math.Max(width, LogicalToDeviceUnits(_nameWidth)) : width;
            }
        }

        public void MeasureCell(DataGridViewCell cell)
        {
            var i = cell.ColumnIndex;
            if (!_savedWidths!.TryGet(_widthKeys[i], DeviceDpi, out _))
                _naturalWidths[i] = Math.Max(_naturalWidths[i], cell.PreferredSize.Width + _extraWidth);
        }

        public void ApplyWidths()
        {
            _applyingWidths = true;
            try
            {
                foreach (DataGridViewColumn column in Columns)
                {
                    var i = column.Index;
                    var width = _savedWidths!.TryGet(_widthKeys[i], DeviceDpi, out var saved) ? saved : _naturalWidths[i];
                    width = Math.Clamp(width, column.MinimumWidth, 65536);
                    if (column.Width != width) column.Width = width;
                }
            }
            finally { _applyingWidths = false; }
        }

        protected override void OnColumnWidthChanged(DataGridViewColumnEventArgs e)
        {
            base.OnColumnWidthChanged(e);
            if (_applyingWidths || _savedWidths == null || Disposing || IsDisposed || e.Column.Index < 0 || e.Column.Index >= _widthKeys.Length) return;
            _savedWidths.Set(_widthKeys[e.Column.Index], e.Column.Width, DeviceDpi);
            _widthChanged?.Invoke();
        }

        public static bool IsNumeric(string key) => key is "hp" or "maxHp" or "attack" or "defense" or "res" or "aspd" or "copies" or "peak" or "rounds" or "damage" or "dps" or "heal" or "seconds";
        protected override void OnColumnHeaderMouseClick(DataGridViewCellMouseEventArgs e)
        {
            base.OnColumnHeaderMouseClick(e);
            if (e.Button != MouseButtons.Left || e.ColumnIndex < 0) return;
            _descending = SortIndex == e.ColumnIndex ? !_descending : IsNumeric(Columns[e.ColumnIndex].Name);
            if (SortIndex >= 0) Columns[SortIndex].HeaderCell.SortGlyphDirection = SortOrder.None;
            SortIndex = e.ColumnIndex;
            Columns[SortIndex].HeaderCell.SortGlyphDirection = _descending ? SortOrder.Descending : SortOrder.Ascending;
            ApplySort();
        }
        public void ApplySort() { if (SortIndex >= 0 && Rows.Count > 1) Sort(new RowComparer(SortIndex, _descending)); }
        sealed class RowComparer(int column, bool descending) : IComparer
        {
            static readonly CompareInfo Chinese = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;
            public int Compare(object? x, object? y)
            {
                if (x is not DataGridViewRow a || y is not DataGridViewRow b) return 0;
                var v = a.Cells[column].Value; var w = b.Cells[column].Value;
                if (v == null && w != null) return 1;
                if (v != null && w == null) return -1;
                var cmp = v switch
                {
                    double d when w is double e => d.CompareTo(e),
                    int i when w is int j => i.CompareTo(j),
                    _ => Chinese.Compare(v?.ToString(), w?.ToString(), CompareOptions.IgnoreCase)
                };
                return cmp == 0 ? string.CompareOrdinal(a.Tag as string, b.Tag as string) : (descending ? -cmp : cmp);
            }
        }
    }
}
