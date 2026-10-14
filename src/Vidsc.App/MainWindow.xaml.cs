using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

using FlyleafLib.MediaPlayer;

using Vidsc.App.ViewModels;

namespace Vidsc.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Player? _player;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private Window? _navigationSurface;


    public MainWindow(MainViewModel viewModel, Player? player)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _player = player;

        InitializeComponent();

        DataContext = _viewModel;

        if (_player is not null)
            Host.Player = _player;

        // Drag and drop is routed through this window so that a dropped file goes through the same
        // generation/invalidation path as the file picker. The host's built-in open-on-drop is
        // disabled on purpose (it would bypass source-generation handling).
        Host.OnSurfaceDrop += OnSurfaceDrop;
        // The pinned host exposes its separately focused WPF surface as a public Window.
        // Preview handlers own only our four navigation keys and unmodified wheel events;
        // handling them before Flyleaf's bubbling bindings prevents conflicting actions.
        Host.SurfaceCreated += OnNavigationSurfaceCreated;
        AttachNavigationSurface();

        Timeline.ScrubStarted += _viewModel.BeginScrub;
        Timeline.ScrubMoved += _viewModel.ScrubTo;
        Timeline.ScrubEnded += _viewModel.EndScrub;

        _viewModel.AppendLog("MainWindow ready. Open a source, or drop a file onto the video surface.");
    }

    private void OnNavigationSurfaceCreated(object? sender, EventArgs e) => AttachNavigationSurface();

    private void AttachNavigationSurface()
    {
        if (ReferenceEquals(_navigationSurface, Host.Surface)) return;
        DetachNavigationSurface();
        _navigationSurface = Host.Surface;
        if (_navigationSurface is null) return;
        _navigationSurface.PreviewKeyDown += OnNavigationKeyDown;
        _navigationSurface.PreviewMouseWheel += OnNavigationWheel;
    }

    private void DetachNavigationSurface()
    {
        if (_navigationSurface is null) return;
        _navigationSurface.PreviewKeyDown -= OnNavigationKeyDown;
        _navigationSurface.PreviewMouseWheel -= OnNavigationWheel;
        _navigationSurface = null;
    }

    /// <summary>Waits until the Flyleaf host has created its D3D surface (needed before playback).</summary>
    public Task<bool> WaitForSurfaceAsync(TimeSpan timeout)
    {
        if (Host.SurfaceHandle != IntPtr.Zero)
            return Task.FromResult(true);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCreated(object? sender, EventArgs args) => completion.TrySetResult(true);

        Host.SurfaceCreated += OnCreated;

        return WaitAsync();

        async Task<bool> WaitAsync()
        {
            try
            {
                Task finished = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(true);
                return ReferenceEquals(finished, completion.Task) && await completion.Task.ConfigureAwait(true);
            }
            finally
            {
                Host.SurfaceCreated -= OnCreated;
            }
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => _viewModel.OpenCommand.Execute(null);

    private void OnRotationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RotationBox?.SelectedItem is not ComboBoxItem item || item.Content is not string text)
            return;

        string digits = new(text.TakeWhile(char.IsDigit).ToArray());

        if (int.TryParse(digits, out int degrees))
            _viewModel.SetRotation(degrees);
    }

    private void OnDesiredTimeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        _viewModel.CommitDesiredExact();
        e.Handled = true;
    }

    private void OnZoomOut(object sender, RoutedEventArgs e) => Timeline.Zoom(2);
    private void OnZoomIn(object sender, RoutedEventArgs e) => Timeline.Zoom(.5);
    private void OnTimelineFit(object sender, RoutedEventArgs e) => Timeline.Fit();
    private void OnTimelineCenter(object sender, RoutedEventArgs e) => Timeline.Center();

    private static bool IsEditingControl(object? source)
    {
        DependencyObject? element = source as DependencyObject;
        while (element is not null)
        {
            if (element is TextBoxBase or ComboBox or Slider) return true;
            element = element is System.Windows.Media.Visual || element is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnNavigationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _shutdownStarted || IsEditingControl(e.OriginalSource) || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Up: _viewModel.NavigateKeyframe(true); break;
            case Key.Down: _viewModel.NavigateKeyframe(false); break;
            case Key.Left: _viewModel.StepBackward(); break;
            case Key.Right: _viewModel.StepForward(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnNavigationWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || _shutdownStarted || IsEditingControl(e.OriginalSource) || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Delta == 0) return;
        _viewModel.NavigateKeyframe(e.Delta > 0);
        e.Handled = true;
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        string? path = TryGetDroppedFile(e);

        if (path is not null)
            _viewModel.RequestOpen(path);

        e.Handled = true;
    }

    private void OnSurfaceDrop(object? sender, DragEventArgs e)
    {
        string? path = TryGetDroppedFile(e);

        if (path is not null)
            _viewModel.RequestOpen(path);
    }

    private static string? TryGetDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return null;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return null;

        string first = files[0];

        // Reject folder drops: opening a source must be explicit and bounded.
        return File.Exists(first) ? first : null;
    }

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;

        // Keep the window alive while the transport chain drains and the engine is disposed, so a
        // blocking Player.Dispose never runs on the dispatcher.
        e.Cancel = true;

        if (_shutdownStarted)
            return;

        _shutdownStarted = true;

        try
        {
            Host.OnSurfaceDrop -= OnSurfaceDrop;
            Host.SurfaceCreated -= OnNavigationSurfaceCreated;
            DetachNavigationSurface();
        }
        catch (Exception)
        {
            // The host may already be torn down.
        }

        try
        {
            await _viewModel.ShutdownAsync();
        }
        catch (Exception exception)
        {
            _viewModel.AppendLog("Shutdown error: " + exception.Message);
        }

        // The Flyleaf WPF host owns a native surface and stays dispatcher-affine.
        try
        {
            Host.Dispose();
        }
        catch (Exception)
        {
            // The host is already torn down by the engine disposal above.
        }

        _shutdownComplete = true;
        // Cleanup may complete synchronously (for example after the smoke path already drained it).
        // Re-entering Close before this Closing event returns is forbidden by WPF.
        _ = Dispatcher.BeginInvoke(Close);
    }
}
