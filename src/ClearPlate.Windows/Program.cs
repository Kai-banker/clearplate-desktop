using ClearPlate.Core;

namespace ClearPlate.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Local\\ClearPlateDesktop-x64", out var firstInstance);
        if (!firstInstance) { MessageBox.Show("光盘打卡工具已在运行，请查看窗口或任务栏托盘。", "光盘打卡"); return 0; }
        try
        {
            if (args.Contains("--smoke-test")) return SmokeTest();
            using var client = CreateClient();
            var store = new WindowsStore();
            using var form = new MainForm(store, client);
            Application.Run(form);
            return 0;
        }
        catch (Exception error)
        {
            if (args.Contains("--smoke-test")) { Console.Error.WriteLine(error); return 1; }
            MessageBox.Show("程序未能启动，为避免丢失提交记录已停止：\r\n" + error.Message, "光盘打卡", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    internal static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private static int SmokeTest()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ClearPlateSmoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WindowsStore(folder);
            var state = new AppState { ScheduleEnabled = false };
            store.Save(new("fake-smoke-token", "fake-account", "测试账号"), state);
            if (File.ReadAllText(Path.Combine(folder, "state.json")).Contains("fake-smoke-token", StringComparison.Ordinal)) throw new Exception("Plaintext token was saved");
            var loaded = store.Load();
            if (loaded.Session?.Token != "fake-smoke-token" || loaded.State.ScheduleEnabled) throw new Exception("DPAPI roundtrip failed");
            using var client = CreateClient();
            using var form = new MainForm(store, client, startTimer: false);
            form.CreateControl();
            if (MainForm.DefaultPhoto().Bytes.Length < 1024 || form.Controls.Count == 0) throw new Exception("UI or bundled photo missing");
            form.Show();
            Application.DoEvents();
            form.Close();
            Console.WriteLine("Windows smoke check passed: DPAPI, form layout, embedded image. No network requests.");
            return 0;
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
