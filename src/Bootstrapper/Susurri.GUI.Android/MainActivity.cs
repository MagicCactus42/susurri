using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Avalonia;
using Avalonia.Android;

namespace Susurri.GUI.Android;

[Activity(
    Label = "susurri",
    Theme = "@style/SusurriTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    private const string Tag = "SusurriCrash";

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder)
            .WithInterFont();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Record(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Record(e.Exception); e.SetObserved(); };

        try
        {
            base.OnCreate(savedInstanceState);
        }
        catch (Exception ex)
        {
            Record(ex);
            ShowFatal(ex);
        }
    }

    private void ShowFatal(Exception ex)
    {
        var text = new TextView(this)
        {
            Text = "Susurri failed to start.\n\n" + ex,
            TextSize = 11,
        };
        text.SetPadding(28, 48, 28, 28);
        text.SetTextIsSelectable(true);

        var scroll = new ScrollView(this);
        scroll.AddView(text);
        SetContentView(scroll, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    private static void Record(Exception? ex)
    {
        if (ex == null)
            return;
        Log.Error(Tag, Throwable.FromException(ex), "unhandled");
        try
        {
            var dir = global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath
                      ?? Path.GetTempPath();
            File.AppendAllText(Path.Combine(dir, "susurri-crash.log"),
                DateTimeOffset.UtcNow.ToString("o") + "\n" + ex + "\n\n");
        }
        catch
        {
        }
    }
}
