namespace LogReader.App.Controls;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using LogReader.App.ViewModels;

/// <summary>Observes live row changes; loading/rebinding never replays a completion.</summary>
public sealed class DashboardLoadIndicator : Grid
{
    public static readonly DependencyProperty HighlightTargetProperty = DependencyProperty.Register(
        nameof(HighlightTarget), typeof(Border), typeof(DashboardLoadIndicator));
    private readonly Ellipse _spinner;
    private readonly RotateTransform _rotation = new();
    private LogGroupViewModel? _group;
    private long _seenRevision;

    public Border? HighlightTarget
    {
        get => (Border?)GetValue(HighlightTargetProperty);
        set => SetValue(HighlightTargetProperty, value);
    }

    internal bool? AnimationPreferenceOverride { get; set; }
    internal int CompletionAnimationCount { get; private set; }
    internal bool IsSpinnerAnimating { get; private set; }

    public DashboardLoadIndicator()
    {
        Width = 14;
        Height = 14;
        IsHitTestVisible = false;
        _spinner = new Ellipse
        {
            Width = 10, Height = 10, StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 2, 5 }, RenderTransform = _rotation,
            RenderTransformOrigin = new Point(.5, .5), Visibility = Visibility.Hidden
        };
        _spinner.SetResourceReference(Shape.StrokeProperty, "AppFocusBorderBrush");
        Children.Add(_spinner);
        Loaded += (_, _) =>
        {
            Attach();
            SystemParameters.StaticPropertyChanged += AnimationPreferenceChanged;
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= AnimationPreferenceChanged;
            Detach();
        };
        DataContextChanged += (_, _) =>
        {
            Detach();
            if (IsLoaded) Attach();
        };
        IsVisibleChanged += (_, _) => UpdateSpinner();
    }

    private bool Animate => AnimationPreferenceOverride ?? SystemParameters.ClientAreaAnimation;

    private void Attach()
    {
        _group = DataContext as LogGroupViewModel;
        if (_group != null)
        {
            _seenRevision = _group.DashboardReadyRevision;
            _group.PropertyChanged += GroupChanged;
        }
        UpdateSpinner();
    }

    private void Detach()
    {
        if (_group != null) _group.PropertyChanged -= GroupChanged;
        _group = null;
        _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        HighlightTarget?.BeginAnimation(OpacityProperty, null);
        _spinner.Visibility = Visibility.Hidden;
        IsSpinnerAnimating = false;
    }

    private void GroupChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LogGroupViewModel.DashboardLoadStatus))
            ToolTip = _group?.DashboardLoadStatus;
        if (args.PropertyName == nameof(LogGroupViewModel.IsDashboardLoading))
            UpdateSpinner();
        if (args.PropertyName != nameof(LogGroupViewModel.DashboardReadyRevision) || _group == null)
            return;
        var revision = _group.DashboardReadyRevision;
        if (revision == _seenRevision) return;
        _seenRevision = revision;
        if (!IsLoaded || !IsVisible || !_group.IsTreeVisible || !Animate || HighlightTarget == null)
            return;
        CompletionAnimationCount++;
        HighlightTarget.BeginAnimation(OpacityProperty, new DoubleAnimation(.14, 0, TimeSpan.FromMilliseconds(900))
            { FillBehavior = FillBehavior.Stop });
    }

    private void UpdateSpinner()
    {
        _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        var loading = IsLoaded && IsVisible && _group?.IsDashboardLoading == true;
        if (loading || !Animate) HighlightTarget?.BeginAnimation(OpacityProperty, null);
        _spinner.Visibility = loading ? Visibility.Visible : Visibility.Hidden;
        IsSpinnerAnimating = loading && Animate;
        if (IsSpinnerAnimating)
            _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
                { RepeatBehavior = RepeatBehavior.Forever });
        ToolTip = _group?.DashboardLoadStatus;
    }

    private void AnimationPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.InvokeAsync(UpdateSpinner);
    }
}
