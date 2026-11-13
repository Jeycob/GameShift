using GameShift.Core;

namespace GameShift.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
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
