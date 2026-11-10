using System.Runtime.InteropServices;
using System.Text.Json;

namespace StrongholdObserver;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--test-host", StringComparer.Ordinal))
        {
            var p = Marshal.StringToHGlobalAnsi("STRONGHOLD-READ");
            try
            {
                Console.WriteLine($"{Environment.ProcessId}:{p.ToInt64():X}");
                Console.Out.Flush();
                Console.ReadLine();
                return 0;
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        if (args.Contains("--probe", StringComparer.Ordinal))
        {
            try
            {
                var result = new ObserverSession(Catalog.Open());
                using (result) Console.WriteLine(JsonSerializer.Serialize(result.Sample(), JsonOptions.Options));
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }
        }
        if (args.Contains("--self-test", StringComparer.Ordinal))
        {
            try { SelfTest.Run(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        ApplicationConfiguration.Initialize();
        if (args.Contains("--form-test", StringComparer.Ordinal))
        {
            var existing = new ObservationHistory(Path.Combine(AppContext.BaseDirectory, "记录", "history.json"));
            if (existing.StorageNote != null) return 1;
            Console.WriteLine($"OK: local history loads ({existing.Matches.Count} matches).");
            var temporary = Directory.CreateTempSubdirectory("observer-form-");
            var widths = Path.Combine(temporary.FullName, "widths.json");
            var history = Path.Combine(temporary.FullName, "history.json");
            try { return TestForm(widths, history); }
            finally { File.Delete(widths); File.Delete(history); temporary.Delete(); }
        }
        try { Application.Run(new MainForm(Catalog.Open(), args.Contains("--connect", StringComparer.Ordinal))); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "卫戍协议 · 实时观察器", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        return 0;
    }

    static int TestForm(string widths, string history)
    {
        var fixture = new ObservationHistory(history);
        var t = DateTimeOffset.Now;
        Snapshot Example(string key, int round, double hp) => new("V3_1", t, "mode_multi_abyss", "终极模拟", round,
            [new("1", "enemy_1216_cansld", "寻路者阵地破坏者", hp, 30_000, 1_800, 1_200, 0, 100)], [], Phase: "作战", SessionKey: key);
        fixture.Record(Example("match-A", 12, 0));
        fixture.Record(Example("match-A", 13, 20_000));
        var live = Example("match-B", 1, 10_000);
        fixture.Record(live with { Enemies = [live.Enemies[0], live.Enemies[0] with { Instance = "2", Name = "冰原御法者", Hp = 25_000, Attack = 9_000 },
            live.Enemies[0] with { Instance = "3", Name = "访问团新兵", Hp = 800, Attack = null }],
            Effects = [new("effect_example_long_identifier_for_width", "强化示例", 10, "", "", [])],
            Allies = [new("a", "char_example_long_identifier_for_width", "烈夏", 2_607.2, 0)],
            CombatSegment = "example", CombatSeconds = 33.03 });
        using var form = new MainForm(Catalog.Open(), false, fixture, new ColumnWidths(widths));
        _ = form.Handle;
        form.PerformLayout();
        var root = form.Controls.OfType<TableLayoutPanel>().Single();
        var tabs = root.Controls.OfType<TabControl>().Single();
        static DataGridView[] Grids(TabControl pages) => pages.TabPages.Cast<TabPage>()
            .Select(page => page.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<DataGridView>().Single()).ToArray();
        var grids = Grids(tabs);
        var grid = grids[0];
        var picks = root.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<ComboBox>()).ToArray();
        if (tabs.TabCount != 3 || picks.Length != 2 || !Equals(grid.Rows[0].Cells[2].Value, 10000d)) return 1;
        if (File.Exists(widths)) return 1;
        foreach (var table in grids)
        {
            int padding = TextRenderer.MeasureText("宽宽宽", table.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            if (table.Rows.Count == 0 || padding < 3 || table.Columns.Cast<DataGridViewColumn>().Any(
                c => c.Width < c.GetPreferredWidth(DataGridViewAutoSizeColumnMode.AllCells, true) + padding)) return 1;
        }
        if (grids[2].Columns["name"].Width < grids[2].LogicalToDeviceUnits(168)) return 1;
        int initial = grid.Columns["hp"].Width;
        var render = typeof(MainForm).GetMethod("RenderView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fixture.Record(live with { Enemies = [live.Enemies[0] with { Hp = 987_654_321.25 }], EnemyListComplete = false });
        render.Invoke(form, null);
        if (grid.Columns["hp"].Width <= initial) return 1;
        fixture.Record(live);
        render.Invoke(form, null);

        var custom = new[] { 111, 132, 153 };
        for (int i = 0; i < grids.Length; i++) grids[i].Columns["id"].Width = custom[i];
        var click = typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        void Click(int col) => click.Invoke(grid, [new DataGridViewCellMouseEventArgs(col, -1, 0, 0, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0))]);
        Click(4);
        if (!Equals(grid.Rows[0].Cells[4].Value, 9000d) || grid.Rows[2].Cells[4].Value != null) return 1;
        Click(4);
        if (!Equals(grid.Rows[0].Cells[4].Value, 1800d) || grid.Rows[2].Cells[4].Value != null) return 1;
        Click(0);
        if (!Equals(grid.Rows[0].Cells[0].Value, "冰原御法者")) return 1;
        picks[0].SelectedIndex = 2;
        picks[1].SelectedIndex = 0;
        if (!Equals(grid.Rows[0].Cells[1].Value, "生命归零") || !Equals(grid.Rows[0].Cells[2].Value, 0d)) return 1;
        picks[0].SelectedIndex = 0;
        if (!Equals(grid.Rows[0].Cells[0].Value, "冰原御法者")) return 1;
        Console.WriteLine("OK: numeric/name sorting, missing values last, previous wave and preserved sort on return.");
        if (grids.Where((g, i) => g.Columns["id"].Width != custom[i]).Any()) return 1;
        form.Close();
        if (!File.Exists(widths)) return 1;
        using var reopened = new MainForm(Catalog.Open(), false, fixture, new ColumnWidths(widths));
        _ = reopened.Handle;
        var reopenedTabs = reopened.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
        if (Grids(reopenedTabs).Where((g, i) => g.Columns["id"].Width != custom[i]).Any()) return 1;
        Console.WriteLine("OK: all three tables fit headers and contents with three extra character widths; manual widths are independent and restored after close and reopen.");
        return 0;
    }
}
