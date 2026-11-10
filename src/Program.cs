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
            var fixture = new ObservationHistory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
            var t = DateTimeOffset.Now;
            Snapshot Example(string key, int round, double hp) => new("V3_1", t, "mode_multi_abyss", "终极模拟", round,
                [new("1", "enemy_1216_cansld", "寻路者阵地破坏者", hp, 30_000, 1_800, 1_200, 0, 100)], [], Phase: "作战", SessionKey: key);
            fixture.Record(Example("match-A", 12, 0));
            fixture.Record(Example("match-A", 13, 20_000));
            var live = Example("match-B", 1, 10_000);
            fixture.Record(live with { Enemies = [live.Enemies[0], live.Enemies[0] with { Instance = "2", Name = "冰原御法者", Hp = 25_000, Attack = 9_000 },
                live.Enemies[0] with { Instance = "3", Name = "访问团新兵", Hp = 800, Attack = null }] });
            using var form = new MainForm(Catalog.Open(), false, fixture);
            _ = form.Handle;
            form.PerformLayout();
            var root = form.Controls.OfType<TableLayoutPanel>().Single();
            var tabs = root.Controls.OfType<TabControl>().Single();
            var grid = tabs.TabPages[0].Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<DataGridView>().Single();
            var picks = root.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<ComboBox>()).ToArray();
            if (tabs.TabCount != 3 || picks.Length != 2 || !Equals(grid.Rows[0].Cells[2].Value, 10000d)) return 1;
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
            return 0;
        }
        try { Application.Run(new MainForm(Catalog.Open(), args.Contains("--connect", StringComparer.Ordinal))); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "卫戍协议 · 实时观察器", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        return 0;
    }
}
