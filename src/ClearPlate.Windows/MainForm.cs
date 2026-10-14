using ClearPlate.Core;
using System.Diagnostics;

namespace ClearPlate.Windows;

public sealed class MainForm : Form
{
    private readonly PlateEngine engine;
    private readonly TextBox group = new() { Width = 200 };
    private readonly TextBox location = new() { Width = 390, PlaceholderText = "当天真实纬度,经度" };
    private readonly TextBox token = new() { Width = 410, UseSystemPasswordChar = true, PlaceholderText = "本人完整 X-Token" };
    private readonly CheckBox schedule = new() { AutoSize = true, Text = "每天 08:00 / 12:00 / 18:00 自动用内置照片打卡" };
    private readonly ComboBox slot = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label account = new() { AutoSize = true };
    private readonly Label overview = new() { AutoSize = true };
    private readonly TextBox record = new() { ReadOnly = true, Multiline = true, Width = 640, Height = 100, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox status = new() { ReadOnly = true, Multiline = true, Width = 640, Height = 80, ScrollBars = ScrollBars.Vertical };
    private readonly List<Button> actionButtons = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "光盘打卡工具" };
    private readonly Image preview;
    public MainForm(WindowsStore store, HttpClient client, bool startTimer = true)
    {
        var saved = store.Load();
        engine = new(new PlateApi(client), saved.State, saved.Session, store.Save);
        Text = "光盘打卡 · Windows x64";
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(710, 860);
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(22) };
        Controls.Add(content);
        var title = new Label { Text = "光盘打卡", AutoSize = true, Font = new Font(Font.FontFamily, 23, FontStyle.Bold), ForeColor = Color.FromArgb(27, 115, 78) };
        content.Controls.Add(title);
        content.Controls.Add(Note("08:00 · 12:00 · 18:00（北京时间）  |  每餐一次，至少间隔 2 小时"));
        content.Controls.Add(account);
        content.Controls.Add(Row(new Label { Text = "组织 ID", Width = 100, AutoSize = false }, group));
        content.Controls.Add(Row(new Label { Text = "打卡位置", Width = 100, AutoSize = false }, location));
        group.Text = engine.State.GroupId; location.Text = engine.State.Location;
        using (var stream = new MemoryStream(DefaultPhoto().Bytes)) preview = new Bitmap(stream);
        content.Controls.Add(Row(new PictureBox { Image = preview, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(82, 82) },
            Note("内置照片：plate.png 原图\r\n自动打卡使用内置照片；手动可选择其他照片。")));
        content.Controls.Add(schedule);
        content.Controls.Add(Note("保持程序运行、电脑唤醒且联网。最小化进入托盘，关闭窗口会退出。"));
        content.Controls.Add(overview);
        slot.Items.AddRange(Schedule.Slots.Cast<object>().ToArray());
        slot.SelectedItem = Schedule.Suggested(DateTimeOffset.UtcNow);
        content.Controls.Add(Row(new Label { Text = "手动打卡时段", Width = 120, AutoSize = false }, slot));
        content.Controls.Add(record);
        content.Controls.Add(Row(ActionButton("检查并同步 token", async () => { SaveSettings(); await engine.CheckAsync(); }),
            ActionButton("用内置照片打卡", async () => { SaveSettings(); await engine.SubmitAsync(SelectedSlot, DefaultPhoto); }),
            ActionButton("选择其他照片", SelectPhotoAsync)));
        content.Controls.Add(Note("首次配置 / 更新本人会话（token 用当前 Windows 用户的 DPAPI 加密保存）"));
        content.Controls.Add(Row(token, ActionButton("核验并保存", async () => {
            SaveSettings();
            if (await engine.ImportAsync(token.Text, user => MessageBox.Show(this,
                $"平台返回账号：{user.Nickname}\r\n用户 ID：{user.AccountId}\r\n确认这是本人账号并绑定？", "绑定本人账号",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)) token.Clear();
        })));
        content.Controls.Add(Row(ActionButton("打开微信小程序", () => {
            Process.Start(new ProcessStartInfo("weixin://dl/business/?appid=wx6a4976bd627fc4ca&path=pages/index/dataHomePage") { UseShellExecute = true });
            return Task.CompletedTask;
        })));
        content.Controls.Add(status);
        content.Controls.Add(Note("查询账号阶段断网可在原 5 分钟窗口内重试；开始上传后不会自动重复提交。\r\n登录过期暂停，需在微信正常登录并导入新 token；目前没有独立续期机制。"));
        schedule.Checked = engine.State.ScheduleEnabled;
        schedule.CheckedChanged += (_, _) => { engine.State.ScheduleEnabled = schedule.Checked; SaveSettings(); };
        slot.SelectedIndexChanged += (_, _) => UpdateUi();
        engine.Changed += UpdateUi;
        timer.Tick += async (_, _) => {
            try { await engine.TickAsync(DefaultPhoto); }
            catch (Exception error) { status.Text = "本地记录保存失败，自动计划已停止：" + error.Message; timer.Stop(); }
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => RestoreWindow());
        menu.Items.Add("退出", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => RestoreWindow();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) { Hide(); tray.Visible = true; } };
        FormClosing += (_, e) => {
            if (engine.Busy) { e.Cancel = true; status.Text = "正在执行请求，请等待结果后再退出。"; return; }
            timer.Stop(); tray.Visible = false;
        };
        if (startTimer) timer.Start();
        UpdateUi();
    }
    private string SelectedSlot => slot.SelectedItem as string ?? "08:00";
    private static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 7, 0, 7) };
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Width = 640, WrapContents = true, Margin = new Padding(0, 5, 0, 5) };
        row.Controls.AddRange(controls); return row;
    }
    private Button ActionButton(string caption, Func<Task> action)
    {
        var button = new Button { Text = caption, AutoSize = true, Padding = new Padding(6), Margin = new Padding(0, 0, 8, 0) };
        button.Click += async (_, _) => {
            try { await action(); }
            catch (Exception error) { status.Text = "操作已停止：" + error.Message; }
        };
        actionButtons.Add(button); return button;
    }
    private void SaveSettings()
    {
        engine.State.GroupId = group.Text.Trim(); engine.State.Location = location.Text.Trim(); engine.Persist();
    }
    private async Task SelectPhotoAsync()
    {
        using var picker = new OpenFileDialog { Filter = "光盘照片|*.png;*.jpg;*.jpeg", Title = "选择当天真实光盘照片", Multiselect = false };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var path = picker.FileName;
        SaveSettings();
        await engine.SubmitAsync(SelectedSlot, () => {
            var info = new FileInfo(path);
            if (info.Length is < 1024 or > 20 * 1024 * 1024) throw new ArgumentException("照片需为 1 KB 至 20 MB。");
            using var image = Image.FromFile(path);
            var bytes = File.ReadAllBytes(path);
            var png = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);
            return (bytes, png ? "image/png" : "image/jpeg", png ? "png" : "jpg");
        });
    }
    internal static (byte[] Bytes, string Mime, string Extension) DefaultPhoto()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ClearPlate.plate.png") ?? throw new IOException("内置照片缺失。");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return (buffer.ToArray(), "image/png", "png");
    }
    private void UpdateUi()
    {
        account.Text = engine.Session is null ? "尚未配置会话" : "账号：" + engine.Session.Nickname;
        status.Text = engine.Status; record.Text = engine.SlotText(SelectedSlot);
        overview.Text = string.Join(" · ", Schedule.Slots.Select(s => {
            var key = Schedule.Key(s, DateTimeOffset.UtcNow);
            return s + " " + (engine.State.Confirmed.Contains(key) ? "成功" : engine.State.Records.GetValueOrDefault(key)?.Outcome ?? "待执行");
        }));
        foreach (var button in actionButtons) button.Enabled = !engine.Busy;
        group.Enabled = location.Enabled = slot.Enabled = token.Enabled = schedule.Enabled = !engine.Busy;
    }
    private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); tray.Visible = false; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tray.Dispose(); preview.Dispose(); }
        base.Dispose(disposing);
    }
}
