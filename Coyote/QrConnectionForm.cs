using QRCoder;
namespace IWCoyoteBridge.Coyote;
public sealed class QrConnectionForm : Form
{
    private readonly CoyoteController controller;
    private readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly TextBox address = new() { Name = "PairSocketAddress", Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button copy = new() { Text = "复制连接地址", AutoSize = true, Enabled = false };
    public string CurrentQrContent { get; private set; } = "";
    private bool pairedClosure;
    public QrConnectionForm(CoyoteController device)
    {
        controller = device;
        Icon = AppIcon.Load();
        Text = "连接 DG-LAB"; ClientSize = new Size(420, 580); MinimumSize = Size;
        StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 7, ColumnCount = 1 };
        foreach (var height in new[] { 310, 42, 36, 38, 42, 42, 36 }) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(grid); grid.Controls.Add(picture);
        grid.Controls.Add(new Label { Text = "DG-LAB APP：Socket控制 → 扫一扫", Dock = DockStyle.Fill });
        grid.Controls.Add(new Label { Text = "协议：V3    服务器：DG-LAB 官方公网中继", Dock = DockStyle.Fill });
        grid.Controls.Add(address); grid.Controls.Add(status);
        grid.Controls.Add(new Label { Text = "手机可使用 4G / 5G / Wi-Fi；电脑需要能访问互联网。", Dock = DockStyle.Fill });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var cancel = new Button { Text = "取消 / 关闭", AutoSize = true };
        copy.Click += (_, _) => { try { if (address.TextLength > 0) Clipboard.SetText(address.Text); } catch (Exception ex) { status.Text = ex.Message; } };
        cancel.Click += async (_, _) => { if (controller.State.Status != CoyoteConnectionStatus.Paired) await controller.StopAsync(true); Close(); };
        buttons.Controls.AddRange([copy, cancel]); grid.Controls.Add(buttons);
        controller.StateChanged += UpdateState;
        FormClosed += (_, _) => { controller.StateChanged -= UpdateState; picture.Image?.Dispose(); };
        FormClosing += (_, _) => { if (!pairedClosure && controller.State.Status is (CoyoteConnectionStatus.WaitingForApp or CoyoteConnectionStatus.Binding)) _ = controller.StopAsync(true); };
        UpdateState(controller.State);
    }
    internal void UpdateState(CoyoteState state)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)(() => UpdateState(state))); } catch (InvalidOperationException) { } return; }
        // Queued notifications can be superseded by a strength update. Always
        // render the latest state instead of dropping a successful pairing.
        state = controller.State;
        if (state.Status == CoyoteConnectionStatus.Paired)
        {
            // Only close the PC pairing dialog, not the APP/socket. Do not run the
            // waiting-for-scan cancellation path for successful automatic closure.
            pairedClosure = true;
            if (IsHandleCreated) Close(); else Shown += (_, _) => Close();
            return;
        }
        var url = controller.PairUrl;
        var payload = url.Length == 0 ? "" : DglabV3Protocol.QrPayload(url);
        if (payload != CurrentQrContent)
        {
            var old = picture.Image; picture.Image = null; old?.Dispose();
            CurrentQrContent = payload; address.Text = url; copy.Enabled = url.Length > 0;
            if (payload.Length > 0)
            {
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
                using var code = new QRCode(data); picture.Image = code.GetGraphic(6);
            }
        }
        status.Text = ConnectionText(state);
    }
    public static string ConnectionText(CoyoteState state) => state.Detail ?? (state.Status switch
    {
        CoyoteConnectionStatus.ConnectingRelay => "正在连接郊狼官方服务器……",
        CoyoteConnectionStatus.WaitingForTargetId => "正在准备扫码连接",
        CoyoteConnectionStatus.WaitingForApp => "● 服务器已连接，请用手机 APP 扫码",
        CoyoteConnectionStatus.Binding => "正在与手机 APP 配对",
        CoyoteConnectionStatus.Paired => state.Ready ? "● 郊狼已连接，实时强度已获取" : "● 已配对，正在读取实时强度",
        CoyoteConnectionStatus.Error => "连接失败，请检查网络后重试",
        _ => "● 郊狼未连接"
    });
}
