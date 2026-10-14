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
    private bool _isSliderDragging;

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

        PositionSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnSliderDragStarted));
        PositionSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnSliderDragCompleted));

        _viewModel.AppendLog("MainWindow ready. Open a source, or drop a file onto the video surface.");
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

    private void OnSliderDragStarted(object sender, DragStartedEventArgs e)
    {
        _isSliderDragging = true;
        _viewModel.BeginScrub();
    }

    private void OnSliderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isSliderDragging = false;
        _viewModel.EndScrub(PositionSlider.Value);
    }

    private void OnPositionSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSliderDragging)
            _viewModel.ScrubTo(e.NewValue);
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
