using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DogSC.Windows.SelfTest;

internal static class ContractUiChecks
{
    public static int Run(string root, string? savedPreference = null)
    {
        Directory.CreateDirectory(root);
        var application = LiveMainChecks.CreateApplication();
        var dispatcher = Dispatcher.CurrentDispatcher;
        int exit = 1;
        _ = dispatcher.InvokeAsync(async () =>
        {
            MainWindow? main = null;
            var originalLanguage = Localization.Language;
            var checks = new List<string>();
            void Check(bool valid, string name)
            {
                if (!valid) throw new Exception("FAIL: " + name);
                checks.Add(name);
            }
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                int localizedChecks = savedPreference is null ? LocalizationChecks.Run(root) : 0;
                if (savedPreference is null) Localization.SetLanguage(InterfaceLanguage.English);
                else Localization.LoadPreference(savedPreference);
                main = new MainWindow();
                await ((Task)typeof(MainWindow).GetField("_deviceDiscovery", flags)!.GetValue(main)!).WaitAsync(TimeSpan.FromSeconds(20));
                Check(((ComboBox)main.FindName("LanguageCombo")).SelectedIndex == 1, "settings selection starts in the restored language");
                Localization.SetLanguage(InterfaceLanguage.Chinese);
                Check(((ComboBox)main.FindName("LanguageCombo")).SelectedIndex == 0, "shared language change synchronizes settings choice");
                bool registered = false;
                main.RecordingMarkerRegistrationChanged += value => registered = value;
                var marker = (Button)main.FindName("MarkerButton");
                void State(RecordingState state, bool active = true, bool closing = false)
                {
                    typeof(MainWindow).GetField("_recordingActive", flags)!.SetValue(main, active);
                    typeof(MainWindow).GetField("_recordingState", flags)!.SetValue(main, (RecordingState?)state);
                    typeof(MainWindow).GetField("_closePending", flags)!.SetValue(main, closing);
                    typeof(MainWindow).GetMethod("UpdateControls", flags)!.Invoke(main, null);
                }
                State(RecordingState.Recording);
                Check(marker.IsEnabled && registered, "recording enables the settings and tray/hotkey marker together");
                State(RecordingState.Paused);
                Check(!marker.IsEnabled && !registered, "pause disables settings and tray/hotkey marker together");
                main.AddRecordingMarker();
                Check(((TextBlock)main.FindName("StatusText")).Text.Contains("暂停时不能添加"), "stale queued marker action explains paused rejection");
                State(RecordingState.Recording, closing: true);
                Check(!marker.IsEnabled && !registered, "closing disables all marker entrances");
                State(RecordingState.Recording, active: false);
                Check(!marker.IsEnabled && !registered, "inactive state cannot enable marker entrances");
                File.WriteAllText(Path.Combine(root, "contracts-ui-result.json"), JsonSerializer.Serialize(new
                    { success = true, controlledState = true, recordingPerformed = false, preferenceLoadedInFreshProcess = savedPreference is not null,
                        localizationChecks = localizedChecks, markerAndLanguageChecks = checks.Count, checks }));
                Console.WriteLine($"UI contracts passed: localization {localizedChecks}, marker/language {checks.Count}.");
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                if (main is not null)
                {
                    typeof(MainWindow).GetField("_recordingActive", flags)!.SetValue(main, false);
                    typeof(MainWindow).GetField("_closePending", flags)!.SetValue(main, false);
                    main.RequestExit();
                }
                Localization.SetLanguage(originalLanguage);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
        GC.KeepAlive(application);
        return exit;
    }
}
