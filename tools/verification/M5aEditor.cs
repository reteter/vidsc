using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Vidsc.App;
using Vidsc.App.ViewModels;
using Vidsc.App.Services;
using Vidsc.App.Controls;
using Vidsc.Core.Projects;
using Vidsc.Core.Time;

// Runs the packaged application's actual startup and bindings. Requires an agreed visible desktop.
static class M5aEditor
{
    [STAThread]
    public static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var checks = new List<string>();
            string work = Environment.GetEnvironmentVariable("VIDSC_M5A_PROOF_DIR")!;
            MainWindow? window = null;
            try
            {
                window = (MainWindow)app.MainWindow;
                var vm = (MainViewModel)window.DataContext;
                string source = Environment.GetEnvironmentVariable("VIDSC_M5A_PROOF_SOURCE")!;
                string originalHash = Hash(source);
                Require(await window.WaitForSurfaceAsync(TimeSpan.FromSeconds(15)), "Native surface unavailable.");
                window.Activate();
                await Until(() => window.IsActive, "Foreground activation unavailable.");
                await vm.OpenAsync(source).WaitAsync(TimeSpan.FromSeconds(30));
                Require(vm.IsOpened, "Source did not open.");
                Require(vm.BuildFrameIndexCommand.CanExecute(null), "Frame-map command unavailable.");
                vm.BuildFrameIndexCommand.Execute(null);
                await vm.WaitForMediaJobIdleAsync().WaitAsync(TimeSpan.FromSeconds(45));
                await ConfirmAt(vm, 1.12, false);
                await ConfirmAt(vm, 1.80, true);
                vm.SegmentName = "First interval";
                vm.AddSegment();
                var first = vm.CaptureProject().Segments.Single();
                Require(first.RequestedStart.AsSeconds() == Rational.Create(28, 25)
                    && first.RequestedEnd.AsSeconds() == Rational.Create(46, 25), "Confirmed In/Out lost frame identity/inclusive end.");
                checks.Add("actual-startup-surface-frame-map-confirmed-in-out");

                vm.CopyStartSeconds = "2.16"; vm.CopyEndSeconds = "2.8"; vm.SegmentName = "Second interval"; vm.AddSegment();
                vm.MoveSegmentUpCommand.Execute(null);
                Require(vm.Segments[0].Name == "Second interval", "Reorder failed.");
                vm.UndoEditorCommand.Execute(null); vm.RedoEditorCommand.Execute(null);
                Require(vm.Segments[0].Name == "Second interval", "Undo/redo failed.");
                vm.RemoveSegmentCommand.Execute(null); vm.UndoEditorCommand.Execute(null);
                Require(vm.Segments.Count == 2, "Remove/undo lost an interval.");
                var timeline = (TimelineControl)window.FindName("Timeline");
                timeline.SetViewport(TimeSpan.TicksPerSecond, 5 * TimeSpan.TicksPerSecond);
                timeline.Zoom(.5);
                Require(vm.EditorViewport.Span == 2.5 * TimeSpan.TicksPerSecond, "Viewport zoom did not reach editor state.");
                timeline.SetViewport(TimeSpan.TicksPerSecond, 5 * TimeSpan.TicksPerSecond);
                string project = Path.Combine(work, "project.vidsc.json");
                vm.SaveProject(project);
                await vm.LoadProjectAsync(project).WaitAsync(TimeSpan.FromSeconds(30));
                Require(vm.Segments.Count == 2 && vm.EditorViewport.Span == 5 * TimeSpan.TicksPerSecond, "Project roundtrip lost edits/viewport.");
                vm.SegmentName = "Recovered interval"; vm.ApplySegment();
                await Until(() => File.Exists(vm.RecoveryPath)
                    && File.ReadAllText(vm.RecoveryPath).Contains("Recovered interval"), "Debounced recovery did not persist edit.");
                await vm.LoadProjectAsync(project).WaitAsync(TimeSpan.FromSeconds(30));
                await vm.RecoverProjectAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(vm.Segments.Any(s => s.Name == "Recovered interval"), "Recovery lost unsaved edit.");
                checks.Add("named-intervals-history-save-reopen-debounced-recovery");
                Require(vm.LoadThumbnailsCommand.CanExecute(null), "Thumbnail worker unavailable.");
                vm.LoadThumbnailsCommand.Execute(null);
                await vm.WaitForMediaJobIdleAsync().WaitAsync(TimeSpan.FromSeconds(45));
                Require(vm.VisibleThumbnails.Count == 8, "Bounded visible thumbnails failed: " + vm.StatusLine);
                checks.Add("viewport-zoom-reopen-and-eight-bounded-native-thumbnails");

                vm.SelectedSegment = vm.Segments[0];
                var otherBeforePreview = vm.Segments[1];
                var preview = vm.PreviewCopyAsync();
                vm.SelectedSegment = otherBeforePreview;
                await preview.WaitAsync(TimeSpan.FromSeconds(90));
                Require(vm.CopyExportSummary.Contains("All video copied"), "Actual copy-plan qualification failed: " + vm.CopyExportSummary);
                Require(vm.Segments[1] == otherBeforePreview && vm.SelectedSegment == otherBeforePreview, "Async preview overwrote another selected segment.");
                vm.SelectedSegment = vm.Segments[0];
                string output1 = Path.Combine(work, "first.mp4"), output2 = Path.Combine(work, "second.mp4");
                vm.CopyDestination = output1; vm.QueueCopyExport();
                vm.SelectedSegment = vm.Segments[1]; vm.CopyDestination = output2; vm.QueueCopyExport();
                await vm.WaitForMediaJobIdleAsync().WaitAsync(TimeSpan.FromSeconds(150));
                Require(vm.ExportQueue.Count == 2 && vm.ExportQueue.All(j => j.Status.StartsWith("Completed"))
                    && File.Exists(output1) && File.Exists(output2), "Sequential native exports failed: " + vm.CopyExportSummary);
                string beforeConflict = Hash(output2);
                bool refused = false;
                try { vm.QueueCopyExport(); } catch (IOException) { refused = true; }
                Require(refused && Hash(output2) == beforeConflict && vm.Segments.Count == 2 && Hash(source) == originalHash,
                    "Conflict changed output/source/editor state.");
                checks.Add("actual-plan-sequential-exports-full-validation-no-clobber-source-preserved");
                string replacement = Path.Combine(work, "alternate-fixture.mp4");
                File.Copy(source, replacement, overwrite: false);
                string cancelled = Path.Combine(work, "cancelled.mp4");
                vm.CopyDestination = cancelled; vm.QueueCopyExport();
                await Until(() => vm.MediaJobProcess is not null, "Queued job did not start a native process.");
                await vm.OpenAsync(replacement).WaitAsync(TimeSpan.FromSeconds(30));
                Require(!vm.IsMediaJobActive && !vm.MediaJobStuck && !File.Exists(cancelled) && vm.Segments.Count == 2,
                    "Source switch lost edits or promoted cancelled output.");
                await vm.LoadProjectAsync(project).WaitAsync(TimeSpan.FromSeconds(30));
                checks.Add("started-queue-source-switch-cancellation-and-rebind");
                var saved = vm.CaptureProject();
                string missing = Path.Combine(work, "missing-source.vidsc.json");
                EditorProjectStore.Save(missing, saved with { Source = saved.Source with { Path = Path.Combine(work, "missing-source.mp4") } });
                await vm.LoadProjectAsync(missing).WaitAsync(TimeSpan.FromSeconds(30));
                bool staleRefused = false;
                try { vm.QueueCopyExport(); } catch (InvalidOperationException) { staleRefused = true; }
                Require(staleRefused && vm.Segments.Count == 2, "Missing source restored export authority or lost edits.");
                checks.Add("missing-source-refusal-retains-edit-intent");
                await vm.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(!vm.IsMediaJobActive && !vm.MediaJobStuck, "Shutdown retained active worker.");
                checks.Add("confirmed-worker-cleanup");
                await File.WriteAllTextAsync(Path.Combine(work, "summary.json"), JsonSerializer.Serialize(new
                { Success = true, Checks = checks, SourceSha256 = originalHash, Assurance = "bounded packaged editor lifecycle; full M1 remains separate" }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(0);
            }
            catch (Exception error)
            {
                await File.WriteAllTextAsync(Path.Combine(work, "failure.txt"), error.ToString());
                await File.WriteAllTextAsync(Path.Combine(work, "partial-checks.json"), JsonSerializer.Serialize(checks));
                if (window?.DataContext is MainViewModel vm)
                {
                    await File.WriteAllLinesAsync(Path.Combine(work, "application-log.txt"), vm.LogLines);
                    try { await vm.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
                }
                app.Shutdown(1);
            }
        }));
        return app.Run();
    }

    private static async Task ConfirmAt(MainViewModel vm, double seconds, bool isOut)
    {
        vm.SeekExact((long)(seconds * TimeSpan.TicksPerSecond));
        await vm.WaitForTransportIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await Until(() =>
        {
            vm.RefreshReadouts();
            try { vm.ConfirmBoundary(isOut); return true; } catch (InvalidOperationException) { return false; }
        }, "Fresh confirmed boundary unavailable: " + vm.ObservedText);
    }
    private static async Task Until(Func<bool> condition, string message)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until) { if (condition()) return; await Task.Delay(100); }
        throw new TimeoutException(message);
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
