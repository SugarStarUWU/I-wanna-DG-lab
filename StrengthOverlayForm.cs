using IWCoyoteBridge.Coyote;
namespace IWCoyoteBridge;

// Display only: this window never sends device commands or authorizes output.
public sealed class StrengthOverlayForm : Form
{
    private readonly Func<CoyoteState> readState;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 100 };
    private readonly Label valueA = Value("OverlayStrengthA"), valueB = Value("OverlayStrengthB");
    public StrengthOverlayForm(Func<CoyoteState> state)
    {
        readState = state;
        Icon = AppIcon.Load();
        Text = "I wanna DG-LAB - 实时强度";
        Name = "StrengthOverlay";
        ClientSize = new Size(360, 155); MinimumSize = new Size(280, 175);
        BackColor = Color.Black; TopMost = true; ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8) };
        grid.ColumnStyles.Add(new(SizeType.Percent, 50)); grid.ColumnStyles.Add(new(SizeType.Percent, 50));
        grid.RowStyles.Add(new(SizeType.Percent, 30)); grid.RowStyles.Add(new(SizeType.Percent, 70));
        grid.Controls.Add(Heading("A:"), 0, 0); grid.Controls.Add(Heading("B:"), 1, 0);
        grid.Controls.Add(valueA, 0, 1); grid.Controls.Add(valueB, 1, 1); Controls.Add(grid);
        var menu = new ContextMenuStrip();
        var pin = new ToolStripMenuItem("始终显示在最上层") { Checked = true, CheckOnClick = true };
        pin.CheckedChanged += (_, _) => TopMost = pin.Checked;
        var green = new ToolStripMenuItem("绿色背景（方便 OBS 去背景）") { CheckOnClick = true };
        green.CheckedChanged += (_, _) => BackColor = green.Checked ? Color.FromArgb(0, 177, 64) : Color.Black;
        menu.Items.AddRange([pin, green]); ContextMenuStrip = menu;
        grid.ContextMenuStrip = menu;
        foreach (Control child in grid.Controls) child.ContextMenuStrip = menu;
        refresh.Tick += (_, _) => RefreshStrength();
        Shown += (_, _) => { RefreshStrength(); refresh.Start(); };
        FormClosed += (_, _) => { refresh.Stop(); refresh.Dispose(); menu.Dispose(); };
        RefreshStrength();
    }
    internal void RefreshStrength()
    {
        var state = readState();
        valueA.Text = state.Ready ? state.ActualA?.ToString() ?? "—" : "—";
        valueB.Text = state.Ready ? state.ActualB?.ToString() ?? "—" : "—";
    }
    private static Label Heading(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold) };
    private static Label Value(string name) => new() { Name = name, Text = "—", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 36, FontStyle.Bold) };
}
