using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ZapretAppPff;

public partial class UpdateNotice : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Func<Task>? _update;
    private bool _dismissing;

    public UpdateNotice(string title, string message, Func<Task>? update)
    {
        InitializeComponent();
        _update = update;
        Title = title;
        Heading.Text = title;
        Message.Text = message;
        Actions.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(update is null ? 6 : 14) };
        _timer.Tick += (_, _) =>
        {
            if (!IsMouseOver && !IsKeyboardFocusWithin)
                Dismiss();
        };
        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - 12);
            Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - 20);
            Animate(opacity: 1, offset: 0, milliseconds: 220);
            _timer.Start();
        };
    }

    private async void UpdateClick(object sender, RoutedEventArgs e)
    {
        if (_dismissing || _update is null)
            return;

        Dismiss();
        await _update();
    }

    private void DismissClick(object sender, RoutedEventArgs e) => Dismiss();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    private void Dismiss()
    {
        if (_dismissing)
            return;

        _dismissing = true;
        _timer.Stop();
        IsHitTestVisible = false;
        Animate(opacity: 0, offset: 10, milliseconds: 180, completed: Close);
    }

    private void Animate(double opacity, double offset, int milliseconds, Action? completed = null)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var fade = new DoubleAnimation(opacity, new Duration(duration));
        if (completed is not null)
            fade.Completed += (_, _) => completed();

        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, new Duration(duration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        BeginAnimation(OpacityProperty, null);
        base.OnClosed(e);
    }
}
