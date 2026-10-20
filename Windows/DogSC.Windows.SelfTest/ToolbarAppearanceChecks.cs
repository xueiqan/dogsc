using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DogSC.Windows.SelfTest;

internal static class ToolbarAppearanceChecks
{
    public static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var application = LiveMainChecks.CreateApplication();
        var dispatcher = Dispatcher.CurrentDispatcher;
        int exit = 1;
        _ = dispatcher.InvokeAsync(async () =>
        {
            RecordingToolbarWindow? bar = null;
            var checks = new List<string>();
            void Check(bool valid, string name)
            {
                if (!valid) throw new Exception("FAIL: " + name);
                checks.Add(name);
            }
            try
            {
                bar = new RecordingToolbarWindow();
                bar.Show();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                var toggle = (CheckBox)bar.FindName("ComposeFinalCheck");
                toggle.ApplyTemplate();
                var tile = (Border)toggle.Template.FindName("ComposeTile", toggle);
                var originalTarget = bar.SelectedTarget;
                int notifications = 0;
                bar.ConfigurationChanged += () => notifications++;
                foreach (bool enabled in new[] { false, true })
                {
                    bar.Update(TimeSpan.Zero, null);
                    bar.Configure("screen", originalTarget, 120, 0, true, false, enabled);
                    Check(bar.ComposeFinal == enabled && bar.FrameRate == 120 && bar.RecordingHeight == 0
                        && bar.SystemAudio && !bar.Microphone, $"configure {enabled}: composition and capture settings are preserved");
                    foreach (RecordingState? state in new RecordingState?[] { null, RecordingState.Starting, RecordingState.Recording,
                                 RecordingState.Paused, RecordingState.Finishing, RecordingState.Complete, RecordingState.Failed })
                    {
                        bar.Update(TimeSpan.FromSeconds(3), state);
                        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        var background = ((SolidColorBrush)tile.Background).Color;
                        var foreground = ((SolidColorBrush)toggle.Foreground).Color;
                        Check(background == (Color)ColorConverter.ConvertFromString(enabled ? "#D6F2E6" : "#E5E7E9")
                            && foreground == (Color)ColorConverter.ConvertFromString(enabled ? "#197648" : "#5D666C")
                            && toggle.IsEnabled == (state is null) && bar.ComposeFinal == enabled,
                            $"{state?.ToString() ?? "Idle"} {enabled}: green on / gray off survives configuration locking");
                    }
                }
                bar.Update(TimeSpan.Zero, null);
                notifications = 0;
                toggle.IsChecked = false;
                Check(notifications == 1 && !bar.ComposeFinal, "idle off sends one configuration change");
                toggle.IsChecked = true;
                Check(notifications == 2 && bar.ComposeFinal, "idle on sends one configuration change");
                Check(toggle.Template.FindName("OffSlash", toggle) is null, "composition off has no red device-disable slash");
                File.WriteAllText(Path.Combine(root, "toolbar-appearance-result.json"), JsonSerializer.Serialize(new
                    { success = true, actualToolbarControl = true, recordingPerformed = false, checks = checks.Count, cases = checks }));
                Console.WriteLine($"Toolbar composition appearance passed: {checks.Count} checks.");
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { bar?.CloseForExit(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        });
        Dispatcher.Run();
        GC.KeepAlive(application);
        return exit;
    }
}
