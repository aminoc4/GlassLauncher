using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace GlassLauncher.Services;

/// <summary>
/// 拖拽时跟着光标浮起的那个图标。
///
/// 一条硬规矩：位置只能改 TranslateTransform，绝对不能改 Canvas.Left/Top。
/// 后者每帧都会让父 Canvas 重新 Arrange，整棵树被卷进布局；前者只走合成层，
/// UI 线程再忙也不会把移动卡成台阶。手感差别就是“贴手”和“拖影子”。
/// </summary>
internal sealed class DragGhost
{
    /// <summary>浮起图标的外框边长，比网格里的 68 稍大一圈。</summary>
    public const double Size = 76;

    private const double IconSize = 56;

    /// <summary>白底板边长，与网格里的 Tile 完全一致。</summary>
    private const double PlateSize = 68;

    private readonly Canvas _layer;
    private readonly Border _body;
    private readonly ScaleTransform _scale;
    private readonly TranslateTransform _move;

    private bool _closed;

    public DragGhost(Canvas layer, ImageSource? icon, string initial, Brush initialBrush)
    {
        _layer = layer;

        var plate = new Border
        {
            Width = PlateSize,
            Height = PlateSize,
            CornerRadius = new CornerRadius(17),
            Background = Application.Current.TryFindResource("IconPlateBrush") as Brush ?? Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var content = new Grid();

        if (icon is null)
        {
            content.Children.Add(new TextBlock
            {
                Text = initial,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = initialBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            var image = new Image
            {
                Source = icon,
                Width = IconSize,
                Height = IconSize,
                Stretch = Stretch.Uniform,
            };

            // 拖拽副本和网格里的图标必须同一套圆角，否则松手那一刻边缘会跳一下
            image.Clip = new RectangleGeometry(new Rect(0, 0, IconSize, IconSize), 12.5, 12.5);

            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            content.Children.Add(image);
        }

        _scale = new ScaleTransform(1, 1);
        _move = new TranslateTransform(0, 0);

        var group = new TransformGroup();
        group.Children.Add(_scale);   // 先绕中心缩放
        group.Children.Add(_move);    // 再整体平移

        plate.Child = content;

        _body = new Border
        {
            Width = Size,
            Height = Size,
            Background = Brushes.Transparent,
            Child = plate,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = group,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.36,
                Color = Colors.Black,

                // 拖拽预览每帧都在动，用性能优先的渲染路径，避免阴影每帧重算高质量卷积
                RenderingBias = RenderingBias.Performance,
            },
        };

        Canvas.SetLeft(_body, 0);
        Canvas.SetTop(_body, 0);
        _layer.Children.Add(_body);
    }

    /// <summary>调试用：报告当前落在哪儿、有没有真的进到画布里。</summary>
    public string Describe() =>
        $"visible={_body.IsVisible} op={_body.Opacity:0.00} size={_body.ActualWidth:0}x{_body.ActualHeight:0} "
        + $"tx={_move.X:0} ty={_move.Y:0} scale={_scale.ScaleX:0.00} parent={(_body.Parent is null ? "null" : _body.Parent.GetType().Name)}";

    /// <summary>把图标中心摆到指定点（DragLayer 坐标）。纯写 Transform，不触发布局。</summary>
    public void MoveTo(Point center)
    {
        if (_closed)
        {
            return;
        }

        _move.X = center.X - (Size / 2);
        _move.Y = center.Y - (Size / 2);
    }

    /// <summary>“拿起”的弹一下：放大 10%，160ms 收尾。macOS 的浮起幅度大约就是这个量级。</summary>
    public void Lift()
    {
        if (_closed)
        {
            return;
        }

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);

        _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1.1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
    }

    /// <summary>
    /// 落到目标格：平移过去 + 略微缩小 + 尾部淡出。
    /// 落点那一刻正好是原条目重新显形的时候，两者接力，看起来就是图标“落回格子”。
    /// </summary>
    public void Settle(Point center, Action onCompleted)
    {
        if (_closed)
        {
            onCompleted();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(170);

        var storyboard = new Storyboard();

        Add(storyboard, _move, TranslateTransform.XProperty, center.X - (Size / 2), duration, ease);
        Add(storyboard, _move, TranslateTransform.YProperty, center.Y - (Size / 2), duration, ease);
        Add(storyboard, _scale, ScaleTransform.ScaleXProperty, 0.94, duration, ease);
        Add(storyboard, _scale, ScaleTransform.ScaleYProperty, 0.94, duration, ease);

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110))
        {
            BeginTime = TimeSpan.FromMilliseconds(60),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };

        Storyboard.SetTarget(fade, _body);
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        storyboard.Children.Add(fade);

        storyboard.Completed += (_, _) =>
        {
            Remove();
            onCompleted();
        };

        storyboard.Begin();
    }

    /// <summary>立刻移除。重复调用无害。</summary>
    public void Remove()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _layer.Children.Remove(_body);
    }

    private static void Add(
        Storyboard storyboard,
        Animatable target,
        DependencyProperty property,
        double to,
        TimeSpan duration,
        IEasingFunction ease)
    {
        var animation = new DoubleAnimation(to, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }
}

/// <summary>
/// 拖拽期间的“格子板”：记下每个条目此刻占的格子，然后让它们顺次让位。
///
/// 做法和 macOS 启动台一致：不画插入线，而是把受影响的那一串图标整体挪一格，
/// 空出来的那一格就是落点。因为位移全部走 RenderTransform 的补间动画，
/// 既不会触发重新布局，也不会出现“整块重排”的跳变。
/// </summary>
internal sealed class SlotShifter
{
    private const double ShiftDurationMs = 180;

    private readonly List<FrameworkElement> _containers = new();
    private readonly List<Rect> _rects = new();
    private readonly List<TranslateTransform> _transforms = new();
    private readonly List<double> _offsetX = new();
    private readonly List<double> _offsetY = new();

    /// <summary>图标中心相对格子顶部的偏移：图标区 80 高、图标 68 顶对齐 → 34。
    /// 拖拽探针与浮起图标都用它，避免两处各写一个数字后逐渐对不上。</summary>
    public const double IconCenterOffsetY = 34;

    public int Count => _rects.Count;

    public bool Captured => _rects.Count > 0;

    /// <summary>
    /// 抓一份当前布局快照。容器拿不全（虚拟化、还没生成）就整体放弃，
    /// 宁可不拖，也不要拖到一半发现一半格子不会让位。
    /// </summary>
    public bool Capture(ItemsControl host)
    {
        Reset();

        int count = host.Items.Count;
        if (count == 0)
        {
            return false;
        }

        // 只在这里做一次布局同步：拖拽开始之后，格子几何不再变化，再也不需要 UpdateLayout
        host.UpdateLayout();

        for (int i = 0; i < count; i++)
        {
            if (host.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                Reset();
                return false;
            }

            Point origin;
            try
            {
                origin = container.TranslatePoint(new Point(0, 0), host);
            }
            catch
            {
                Reset();
                return false;
            }

            var transform = new TranslateTransform();

            _containers.Add(container);
            _rects.Add(new Rect(origin, new Size(container.ActualWidth, container.ActualHeight)));
            _transforms.Add(transform);
            _offsetX.Add(0);
            _offsetY.Add(0);

            container.RenderTransform = transform;
        }

        return _rects.Count > 0;
    }

    public Rect RectAt(int index) =>
        index >= 0 && index < _rects.Count ? _rects[index] : Rect.Empty;

    /// <summary>第 index 个条目的图标中心（相对网格）。图标区 80 高，图标 68 顶对齐，故中心在 y=34。</summary>
    public Point IconCenterAt(int index)
    {
        Rect rect = RectAt(index);
        return rect.IsEmpty
            ? new Point(0, 0)
            : new Point(rect.Left + (rect.Width / 2), rect.Top + IconCenterOffsetY);
    }

    /// <summary>
    /// 把 from 移动到 to 之后，各格子的位移。
    /// 向前移：to..from-1 依次进一格；向后移：from+1..to 依次退一格。
    /// </summary>
    public void ApplyGap(int from, int to)
    {
        if (!Captured || from == to)
        {
            return;
        }

        for (int i = 0; i < _rects.Count; i++)
        {
            int slot = i;

            if (to < from && i >= to && i < from)
            {
                slot = i + 1;
            }
            else if (to > from && i > from && i <= to)
            {
                slot = i - 1;
            }

            if (slot == i)
            {
                Animate(i, 0, 0);
                continue;
            }

            Rect target = _rects[slot];
            Animate(i, target.Left - _rects[i].Left, target.Top - _rects[i].Top);
        }
    }

    /// <summary>
    /// 清掉所有位移。调用时机很关键：必须在集合已经 Move 完、但还没重新布局的那一瞬间，
    /// 此时每个容器的真实位置恰好等于它刚才被推到的位置，位移归零不会产生任何跳变。
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _transforms.Count; i++)
        {
            TranslateTransform transform = _transforms[i];

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = 0;
            transform.Y = 0;

            if (i < _containers.Count)
            {
                _containers[i].RenderTransform = null;
            }
        }

        _containers.Clear();
        _rects.Clear();
        _transforms.Clear();
        _offsetX.Clear();
        _offsetY.Clear();
    }

    private void Animate(int index, double x, double y)
    {
        // 目标没变就别重启动画：拖拽时 ApplyGap 会被频繁调用，
        // 每帧重头开始补间会让图标一直在起步阶段抖动。
        if (Math.Abs(_offsetX[index] - x) < 0.5 && Math.Abs(_offsetY[index] - y) < 0.5)
        {
            return;
        }

        _offsetX[index] = x;
        _offsetY[index] = y;

        TranslateTransform transform = _transforms[index];
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(ShiftDurationMs);

        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });

        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }
}
