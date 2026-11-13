using GameShift.Core;
using System.Text.Json;

namespace GameShift.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--probe-strata")
        {
            if (!Path.IsPathFullyQualified(args[1]) || File.Exists(args[1])) throw new ArgumentException("Choose a new absolute JSON report path.");
            try
            {
                var configuration = ControlFiles.Read<Settings>(Path.Combine(ControlFiles.DefaultDirectory, "settings.json")) ?? new Settings();
                configuration.Validate();
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
                var status = new StrataClient(http).ProbeAuthenticationAsync(configuration, CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { authenticated = status == 200, statusCode = status, readOnly = true, time = DateTimeOffset.UtcNow }, Settings.Json));
            }
            catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or InvalidOperationException or OperationCanceledException)
            {
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { authenticated = false, errorType = error.GetType().Name, readOnly = true, time = DateTimeOffset.UtcNow }, Settings.Json));
            }
            return;
        }
        if (args.Length == 2 && args[0] == "--scan-games")
        {
            if (!Path.IsPathFullyQualified(args[1]) || File.Exists(args[1])) throw new ArgumentException("Choose a new absolute JSON report path.");
            File.WriteAllText(args[1], JsonSerializer.Serialize(GameLibraryScanner.ScanInstalled(), Settings.Json));
            return;
        }
        var smoke = args.Length == 2 && args[0] == "--ui-smoke";
        if (smoke)
        {
            if (!Path.IsPathFullyQualified(args[1])) throw new ArgumentException("Use an absolute screenshot path.");
            using var form = new MainForm(preview: true);
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle with { Width = form.Width, Height = form.Height });
            bitmap.Save(args[1]);
            return;
        }
        using var mutex = new Mutex(true, "Local\\GameShift.CurrentUser." + Environment.UserName, out var first);
        if (!first)
        {
            MessageBox.Show("GameShift is already running. Open it from the notification area.", "GameShift");
            return;
        }
        Application.Run(new MainForm(preview: false, minimized: args.Contains("--minimized")));
    }
}
