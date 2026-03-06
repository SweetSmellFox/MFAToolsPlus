using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SukiUI.Controls;

[TemplatePart(PART_Image, typeof(Image))]
[TemplatePart(PART_Layer, typeof(VisualLayerManager))]
[PseudoClasses(PC_Moving)]
public class SukiImageViewer : TemplatedControl
{
    public SukiImageViewer()
    {
        SelectionRects = new AvaloniaList<Rect>();
        ZoomInCommand = new RelayCommand(_ => ZoomIn());
        ZoomOutCommand = new RelayCommand(_ => ZoomOut());
    }
    public const string PART_Image = "PART_Image";
    public const string PART_Layer = "PART_Layer";
    public const string PC_Moving = ":moving";

    private Image? _image;
    private Point? _lastClickPoint;
    private Point? _lastLocation;
    private bool _moving;
    private bool _ctrlDragActive;

    public static readonly StyledProperty<Control?> OverlayerProperty = AvaloniaProperty.Register<SukiImageViewer, Control?>(
        nameof(Overlayer));

    public Control? Overlayer
    {
        get => GetValue(OverlayerProperty);
        set => SetValue(OverlayerProperty, value);
    }

    public static readonly StyledProperty<IImage?> SourceProperty = Image.SourceProperty.AddOwner<SukiImageViewer>();

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DirectProperty<SukiImageViewer, double> ScaleProperty = AvaloniaProperty.RegisterDirect<SukiImageViewer, double>(
        nameof(Scale), o => o.Scale, (o, v) => o.Scale = v, unsetValue: 1);

    public static readonly StyledProperty<bool> IsDragEnabledProperty = AvaloniaProperty.Register<SukiImageViewer, bool>(
        nameof(IsDragEnabled), defaultValue: true);

    public static readonly StyledProperty<bool> ShowZoomToolbarProperty = AvaloniaProperty.Register<SukiImageViewer, bool>(
        nameof(ShowZoomToolbar), defaultValue: false);

    public static readonly StyledProperty<bool> UseCustomZoomToolbarProperty = AvaloniaProperty.Register<SukiImageViewer, bool>(
        nameof(UseCustomZoomToolbar), defaultValue: false);

    public static readonly DirectProperty<SukiImageViewer, bool> ShowDefaultZoomToolbarProperty = AvaloniaProperty.RegisterDirect<SukiImageViewer, bool>(
        nameof(ShowDefaultZoomToolbar), o => o.ShowDefaultZoomToolbar);

    public static readonly StyledProperty<bool> ShowCheckerboardBackgroundProperty = AvaloniaProperty.Register<SukiImageViewer, bool>(
        nameof(ShowCheckerboardBackground), defaultValue: false);

    public static readonly StyledProperty<AvaloniaList<Rect>> SelectionRectsProperty = AvaloniaProperty.Register<SukiImageViewer, AvaloniaList<Rect>>(
        nameof(SelectionRects));

    public AvaloniaList<Rect> SelectionRects
    {
        get => GetValue(SelectionRectsProperty) ?? new AvaloniaList<Rect>();
        set => SetValue(SelectionRectsProperty, value);
    }


    public bool IsDragEnabled
    {
        get => GetValue(IsDragEnabledProperty);
        set => SetValue(IsDragEnabledProperty, value);
    }

    public bool ShowZoomToolbar
    {
        get => GetValue(ShowZoomToolbarProperty);
        set => SetValue(ShowZoomToolbarProperty, value);
    }

    public bool UseCustomZoomToolbar
    {
        get => GetValue(UseCustomZoomToolbarProperty);
        set => SetValue(UseCustomZoomToolbarProperty, value);
    }

    public bool ShowDefaultZoomToolbar
    {
        get;
        private set => SetAndRaise(ShowDefaultZoomToolbarProperty, ref field, value);
    }

    public bool ShowCheckerboardBackground
    {
        get => GetValue(ShowCheckerboardBackgroundProperty);
        set => SetValue(ShowCheckerboardBackgroundProperty, value);
    }

    public static readonly StyledProperty<Rect> SelectionRectProperty = AvaloniaProperty.Register<SukiImageViewer, Rect>(
        nameof(SelectionRect));

    public Rect SelectionRect
    {
        get => GetValue(SelectionRectProperty);
        set => SetValue(SelectionRectProperty, value);
    }

    public static readonly DirectProperty<SukiImageViewer, bool> IsSelectionSinglePixelProperty =
        AvaloniaProperty.RegisterDirect<SukiImageViewer, bool>(
            nameof(IsSelectionSinglePixel),
            o => o.IsSelectionSinglePixel);

    public bool IsSelectionSinglePixel
    {
        get;
        private set => SetAndRaise(IsSelectionSinglePixelProperty, ref field, value);
    }

    public static readonly StyledProperty<bool> HasBrushPreviewProperty = AvaloniaProperty.Register<SukiImageViewer, bool>(
        nameof(HasBrushPreview));

    public bool HasBrushPreview
    {
        get => GetValue(HasBrushPreviewProperty);
        set => SetValue(HasBrushPreviewProperty, value);
    }

    public static readonly StyledProperty<Point> BrushPreviewPointProperty = AvaloniaProperty.Register<SukiImageViewer, Point>(
        nameof(BrushPreviewPoint));

    public Point BrushPreviewPoint
    {
        get => GetValue(BrushPreviewPointProperty);
        set => SetValue(BrushPreviewPointProperty, value);
    }

    public static readonly StyledProperty<double> BrushPreviewSizeProperty = AvaloniaProperty.Register<SukiImageViewer, double>(
        nameof(BrushPreviewSize), 1);

    public double BrushPreviewSize
    {
        get => GetValue(BrushPreviewSizeProperty);
        set => SetValue(BrushPreviewSizeProperty, value);
    }

    public static readonly DirectProperty<SukiImageViewer, Rect> BrushPreviewRectProperty =
        AvaloniaProperty.RegisterDirect<SukiImageViewer, Rect>(
            nameof(BrushPreviewRect),
            o => o.BrushPreviewRect);

    public Rect BrushPreviewRect
    {
        get => _brushPreviewRect;
        private set => SetAndRaise(BrushPreviewRectProperty, ref _brushPreviewRect, value);
    }

    public static readonly StyledProperty<IBrush?> BrushPreviewStrokeProperty = AvaloniaProperty.Register<SukiImageViewer, IBrush?>(
        nameof(BrushPreviewStroke), defaultValue: new SolidColorBrush(Colors.LimeGreen));

    public IBrush? BrushPreviewStroke
    {
        get => GetValue(BrushPreviewStrokeProperty);
        set => SetValue(BrushPreviewStrokeProperty, value);
    }

    public static readonly StyledProperty<IBrush?> BrushPreviewFillProperty = AvaloniaProperty.Register<SukiImageViewer, IBrush?>(
        nameof(BrushPreviewFill), defaultValue: new SolidColorBrush(Color.FromArgb(30, 0, 255, 0)));

    public IBrush? BrushPreviewFill
    {
        get => GetValue(BrushPreviewFillProperty);
        set => SetValue(BrushPreviewFillProperty, value);
    }

    public static readonly StyledProperty<bool> HasSwipeArrowProperty = AvaloniaProperty.Register<SukiImageViewer, bool>(
        nameof(HasSwipeArrow));

    public bool HasSwipeArrow
    {
        get => GetValue(HasSwipeArrowProperty);
        set => SetValue(HasSwipeArrowProperty, value);
    }

    public static readonly StyledProperty<Geometry?> SwipeArrowGeometryProperty = AvaloniaProperty.Register<SukiImageViewer, Geometry?>(
        nameof(SwipeArrowGeometry));

    public Geometry? SwipeArrowGeometry
    {
        get => GetValue(SwipeArrowGeometryProperty);
        set => SetValue(SwipeArrowGeometryProperty, value);
    }

    public static readonly StyledProperty<IBrush?> SwipeArrowStrokeProperty = AvaloniaProperty.Register<SukiImageViewer, IBrush?>(
        nameof(SwipeArrowStroke), defaultValue: new SolidColorBrush(Colors.Orange));

    public IBrush? SwipeArrowStroke
    {
        get => GetValue(SwipeArrowStrokeProperty);
        set => SetValue(SwipeArrowStrokeProperty, value);
    }

    public static readonly StyledProperty<bool> HasSelectionProperty = AvaloniaProperty.Register<SukiImageViewer, bool>(
        nameof(HasSelection));

    public bool HasSelection
    {
        get => GetValue(HasSelectionProperty);
        set => SetValue(HasSelectionProperty, value);
    }

    public static readonly StyledProperty<IBrush?> SelectionStrokeProperty = AvaloniaProperty.Register<SukiImageViewer, IBrush?>(
        nameof(SelectionStroke), defaultValue: new SolidColorBrush(Colors.DodgerBlue));

    public IBrush? SelectionStroke
    {
        get => GetValue(SelectionStrokeProperty);
        set => SetValue(SelectionStrokeProperty, value);
    }

    public static readonly StyledProperty<IBrush?> SelectionFillProperty = AvaloniaProperty.Register<SukiImageViewer, IBrush?>(
        nameof(SelectionFill), defaultValue: new SolidColorBrush(Color.FromArgb(40, 64, 128, 255)));

    public IBrush? SelectionFill
    {
        get => GetValue(SelectionFillProperty);
        set => SetValue(SelectionFillProperty, value);
    }

    public static readonly StyledProperty<Rect> SecondarySelectionRectProperty = AvaloniaProperty.Register<SukiImageViewer, Rect>(
        nameof(SecondarySelectionRect));

    public Rect SecondarySelectionRect
    {
        get => GetValue(SecondarySelectionRectProperty);
        set => SetValue(SecondarySelectionRectProperty, value);
    }

    public static readonly DirectProperty<SukiImageViewer, bool> IsSecondarySelectionSinglePixelProperty =
        AvaloniaProperty.RegisterDirect<SukiImageViewer, bool>(
            nameof(IsSecondarySelectionSinglePixel),
            o => o.IsSecondarySelectionSinglePixel);

    public bool IsSecondarySelectionSinglePixel
    {
        get;
        private set => SetAndRaise(IsSecondarySelectionSinglePixelProperty, ref field, value);
    }

    public static readonly StyledProperty<bool> HasSecondarySelectionProperty = AvaloniaProperty.Register<SukiImageViewer, bool>(
        nameof(HasSecondarySelection));

    public bool HasSecondarySelection
    {
        get => GetValue(HasSecondarySelectionProperty);
        set => SetValue(HasSecondarySelectionProperty, value);
    }

    public static readonly StyledProperty<IBrush?> SecondarySelectionStrokeProperty = AvaloniaProperty.Register<SukiImageViewer, IBrush?>(
        nameof(SecondarySelectionStroke), defaultValue: new SolidColorBrush(Colors.Orange));

    public IBrush? SecondarySelectionStroke
    {
        get => GetValue(SecondarySelectionStrokeProperty);
        set => SetValue(SecondarySelectionStrokeProperty, value);
    }

    public static readonly StyledProperty<IBrush?> SecondarySelectionFillProperty = AvaloniaProperty.Register<SukiImageViewer, IBrush?>(
        nameof(SecondarySelectionFill), defaultValue: new SolidColorBrush(Color.FromArgb(40, 255, 165, 0)));

    public IBrush? SecondarySelectionFill
    {
        get => GetValue(SecondarySelectionFillProperty);
        set => SetValue(SecondarySelectionFillProperty, value);
    }

    public double Scale
    {
        get;
        set => SetAndRaise(ScaleProperty, ref field, value);
    }

    public static readonly DirectProperty<SukiImageViewer, double> MinScaleProperty = AvaloniaProperty.RegisterDirect<SukiImageViewer, double>(
        nameof(MinScale), o => o.MinScale, (o, v) => o.MinScale = v, unsetValue: 0.1);

    public double MinScale
    {
        get;
        set => SetAndRaise(MinScaleProperty, ref field, value);
    }

    public static readonly DirectProperty<SukiImageViewer, double> MaxScaleProperty = AvaloniaProperty.RegisterDirect<SukiImageViewer, double>(
        nameof(MaxScale), o => o.MaxScale, (o, v) => o.MaxScale = v, unsetValue: 1);

    public double MaxScale
    {
        get;
        set => SetAndRaise(MaxScaleProperty, ref field, value);
    }

    public static readonly DirectProperty<SukiImageViewer, double> TranslateXProperty = AvaloniaProperty.RegisterDirect<SukiImageViewer, double>(
        nameof(TranslateX), o => o.TranslateX, (o, v) => o.TranslateX = v, unsetValue: 0);

    public double TranslateX
    {
        get;
        set => SetAndRaise(TranslateXProperty, ref field, value);
    }

    public static readonly DirectProperty<SukiImageViewer, double> TranslateYProperty =
        AvaloniaProperty.RegisterDirect<SukiImageViewer, double>(
            nameof(TranslateY), o => o.TranslateY, (o, v) => o.TranslateY = v, unsetValue: 0);

    public double TranslateY
    {
        get;
        set => SetAndRaise(TranslateYProperty, ref field, value);
    }

    public static readonly StyledProperty<double> SmallChangeProperty = AvaloniaProperty.Register<SukiImageViewer, double>(
        nameof(SmallChange), defaultValue: 1);

    public double SmallChange
    {
        get => GetValue(SmallChangeProperty);
        set => SetValue(SmallChangeProperty, value);
    }

    public static readonly StyledProperty<double> LargeChangeProperty = AvaloniaProperty.Register<SukiImageViewer, double>(
        nameof(LargeChange), defaultValue: 20);

    public double LargeChange
    {
        get => GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    public ICommand ZoomInCommand { get; }

    public ICommand ZoomOutCommand { get; }

    public static readonly StyledProperty<Stretch> StretchProperty =
        Image.StretchProperty.AddOwner<SukiImageViewer>(new StyledPropertyMetadata<Stretch>(Stretch.Uniform));

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public static readonly StyledProperty<BitmapInterpolationMode> BitmapInterpolationModeProperty =
        AvaloniaProperty.Register<SukiImageViewer, BitmapInterpolationMode>(
            nameof(BitmapInterpolationMode),
            defaultValue: BitmapInterpolationMode.None);

    public BitmapInterpolationMode BitmapInterpolationMode
    {
        get => GetValue(BitmapInterpolationModeProperty);
        set => SetValue(BitmapInterpolationModeProperty, value);
    }

    private double _sourceMinScale = 0.1;
    private double _sourceMaxScale = 1;
    private Rect _brushPreviewRect;

    static SukiImageViewer()
    {
        CleanupOnStartup();
        FocusableProperty.OverrideDefaultValue<SukiImageViewer>(true);
        OverlayerProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnOverlayerChanged(e));
        SourceProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnSourceChanged(e));
        TranslateXProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnTranslateXChanged(e));
        TranslateYProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnTranslateYChanged(e));
        StretchProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnStretchChanged(e));
        MinScaleProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnMinScaleChanged(e));
        MaxScaleProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnMaxScaleChanged(e));
        SelectionRectProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnSelectionRectChanged(e));
        SecondarySelectionRectProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnSecondarySelectionRectChanged(e));
        BoundsProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnBoundsChanged(e));
        BitmapInterpolationModeProperty.Changed.AddClassHandler<SukiImageViewer>((o, e) => o.OnBitmapInterpolationModeChanged(e));
        BrushPreviewPointProperty.Changed.AddClassHandler<SukiImageViewer>((o, _) => o.UpdateBrushPreviewRect());
        BrushPreviewSizeProperty.Changed.AddClassHandler<SukiImageViewer>((o, _) => o.UpdateBrushPreviewRect());
        ShowZoomToolbarProperty.Changed.AddClassHandler<SukiImageViewer>((o, _) => o.UpdateZoomToolbarVisibility());
        UseCustomZoomToolbarProperty.Changed.AddClassHandler<SukiImageViewer>((o, _) => o.UpdateZoomToolbarVisibility());
    }

    private void OnBitmapInterpolationModeChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (_image is null) return;
        var mode = args.GetNewValue<BitmapInterpolationMode>();
        RenderOptions.SetBitmapInterpolationMode(_image, mode);
    }

    private void UpdateZoomToolbarVisibility()
    {
        ShowDefaultZoomToolbar = ShowZoomToolbar && !UseCustomZoomToolbar;
    }

    private void UpdateBrushPreviewRect()
    {
        var sizeValue = Math.Max(1, (int)Math.Round(BrushPreviewSize));
        var half = (sizeValue - 1) / 2.0;
        var x = Math.Floor(BrushPreviewPoint.X - half);
        var y = Math.Floor(BrushPreviewPoint.Y - half);
        BrushPreviewRect = new Rect(x, y, sizeValue, sizeValue);
    }


    private void OnTranslateYChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (_moving) return;
        var newValue = args.GetNewValue<double>();
        _lastLocation = _lastLocation?.WithY(newValue) ?? new Point(0, newValue);
    }

    private void OnTranslateXChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (_moving) return;
        var newValue = args.GetNewValue<double>();
        _lastLocation = _lastLocation?.WithX(newValue) ?? new Point(newValue, 0);
    }

    private void OnOverlayerChanged(AvaloniaPropertyChangedEventArgs args)
    {
        var control = args.GetNewValue<Control?>();
        if (control is { } c)
        {
            AdornerLayer.SetAdorner(this, c);
        }
    }
    private void OnSelectionRectChanged(AvaloniaPropertyChangedEventArgs args)
    {
        var rect = args.GetNewValue<Rect>();
        SelectionRects ??= new AvaloniaList<Rect>();
        SelectionRects.Clear();
        if (rect.Width > 0 && rect.Height > 0)
        {
            SelectionRects.Add(rect);
            HasSelection = true;
            IsSelectionSinglePixel = rect.Width <= 1 && rect.Height <= 1;
        }
        else
        {
            HasSelection = false;
            IsSelectionSinglePixel = false;
        }
    }

    private void OnSecondarySelectionRectChanged(AvaloniaPropertyChangedEventArgs args)
    {
        var rect = args.GetNewValue<Rect>();
        IsSecondarySelectionSinglePixel = rect.Width > 0 && rect.Height > 0 && rect.Width <= 1 && rect.Height <= 1;
    }

    private void OnSourceChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (!IsLoaded) return;
        IImage image = args.GetNewValue<IImage>();
        if (image is null)
        {
            return;
        }
        Size size = image.Size;
        double width = this.Bounds.Width;
        double height = this.Bounds.Height;
        if (_image is not null)
        {
            var sameSize = Math.Abs(_image.Width - size.Width) < 0.01
                           && Math.Abs(_image.Height - size.Height) < 0.01;

            _image.Width = size.Width;
            _image.Height = size.Height;
            RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode);

            if (sameSize)
            {
                return;
            }
        }
        Scale = GetScaleRatio(width / size.Width, height / size.Height, this.Stretch);
        _sourceMinScale = Math.Max(MinScale, Math.Min(width * MinScale / size.Width, height * MinScale / size.Height));
        _sourceMaxScale = Math.Max(MaxScale, _sourceMinScale);
        Scale = Math.Clamp(Scale, _sourceMinScale, _sourceMaxScale);
    }

    private void OnStretchChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (_image is null) return;
        var stretch = args.GetNewValue<Stretch>();
        Scale = GetScaleRatio(Width / _image.Width, Height / _image.Height, stretch);
        _sourceMinScale = _image is not null ? Math.Max(MinScale, Math.Min(Width * MinScale / _image.Width, Height * MinScale / _image.Height)) : MinScale;
        _sourceMaxScale = Math.Max(MaxScale, _sourceMinScale);
        Scale = Math.Clamp(Scale, _sourceMinScale, _sourceMaxScale);
    }

    private void OnMinScaleChanged(AvaloniaPropertyChangedEventArgs _)
    {
        _sourceMinScale = _image is not null ? Math.Max(MinScale, Math.Min(Width * MinScale / _image.Width, Height * MinScale / _image.Height)) : MinScale;
        _sourceMaxScale = Math.Max(MaxScale, _sourceMinScale);

        Scale = Math.Clamp(Scale, _sourceMinScale, _sourceMaxScale);
    }

    private void OnMaxScaleChanged(AvaloniaPropertyChangedEventArgs _)
    {
        _sourceMaxScale = Math.Max(MaxScale, _sourceMinScale);
        Scale = Math.Clamp(Scale, _sourceMinScale, _sourceMaxScale);
    }

    private void OnBoundsChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (!IsLoaded || _image is null || Source is null) return;

        var newBounds = args.GetNewValue<Rect>();
        double width = newBounds.Width;
        double height = newBounds.Height;

        if (width <= 0 || height <= 0) return;

        // 重新计算最小缩放系数
        _sourceMinScale = Math.Max(MinScale, Math.Min(width * MinScale / _image.Width, height * MinScale / _image.Height));
        _sourceMaxScale = Math.Max(MaxScale, _sourceMinScale);

        Scale = Math.Clamp(Scale, _sourceMinScale, _sourceMaxScale);

        // 重新应用边界限制，确保图片不会跑到看不见的地方
        ApplyClampedTranslation(TranslateX, TranslateY);
    }

    private double GetScaleRatio(double widthRatio, double heightRatio, Stretch stretch)
    {
        return stretch switch
        {
            Stretch.Fill => 1d,
            Stretch.None => 1d,
            Stretch.Uniform => Math.Min(widthRatio, heightRatio),
            Stretch.UniformToFill => Math.Max(widthRatio, heightRatio),
            _ => 1d,
        };
    }

    /// <summary>
    /// 计算并限制平移值，确保图片不会移出可视区域太多
    /// </summary>
    private (double clampedX, double clampedY) ClampTranslation(double translateX, double translateY)
    {
        if (_image is null) return (translateX, translateY);

        double viewportWidth = Bounds.Width;
        double viewportHeight = Bounds.Height;
        double scaledWidth = _image.Width * Scale;
        double scaledHeight = _image.Height * Scale;

        // 计算允许的最大平移量
        // 如果图片比视口大，允许移动到图片边缘对齐视口边缘
        // 如果图片比视口小，允许图片在视口内移动但不能完全移出
        double maxTranslateX = Math.Max(0, (scaledWidth - viewportWidth) / 2) + Math.Min(scaledWidth, viewportWidth) / 2;
        double maxTranslateY = Math.Max(0, (scaledHeight - viewportHeight) / 2) + Math.Min(scaledHeight, viewportHeight) / 2;

        double clampedX = Math.Clamp(translateX, -maxTranslateX, maxTranslateX);
        double clampedY = Math.Clamp(translateY, -maxTranslateY, maxTranslateY);

        return (clampedX, clampedY);
    }

    /// <summary>
    /// 应用限制后的平移值
    /// </summary>
    private void ApplyClampedTranslation(double translateX, double translateY)
    {
        var (clampedX, clampedY) = ClampTranslation(translateX, translateY);
        TranslateX = clampedX;
        TranslateY = clampedY;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _image = e.NameScope.Get<Image>(PART_Image);
        SelectionRects ??= new AvaloniaList<Rect>();

        if (Overlayer is { } c)
        {
            AdornerLayer.SetAdorner(this, c);
        }

        // 设置图像渲染质量
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode);

        UpdateZoomToolbarVisibility();

        // 设置右键菜单
        SetupContextMenu();
    }

    private void SetupContextMenu()
    {
        if (_image == null) return;

        var contextMenu = new ContextMenu();
        var copyMenuItem = new MenuItem();
        copyMenuItem.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension("STRING_MENU_COPY"));
        copyMenuItem.Click += OnCopyImageClick;
        contextMenu.Items.Add(copyMenuItem);
        this.ContextMenu = contextMenu;
    }

    private void OnCopyImageClick(object? sender, RoutedEventArgs e)
    {
        _ = CopyImageToClipboardAsync();
    }


    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "MFAToolsPlus_Clipboard");

    // 应用启动时调用，清理旧的临时文件
    public static void CleanupOnStartup()
    {
        try
        {
            if (Directory.Exists(TempDir))
            {
                Directory.Delete(TempDir, true);
            }
        }
        catch
        {
            /* 忽略清理失败 */
        }
    }

    public static string GetTempFilePath(string extension = ".png")
    {
        if (!Directory.Exists(TempDir))
            Directory.CreateDirectory(TempDir);
        return Path.Combine(TempDir, $"clipboard_{Guid.NewGuid()}{extension}");
    }

    public async Task CopyImageToClipboardAsync()
    {
        if (Source is not Bitmap bitmap) return;

        var topLevel = this.GetVisualRoot() as TopLevel;
        var clipboard = topLevel?.Clipboard;
        if (clipboard == null) return;

        try
        {
            var tempPath = GetTempFilePath();
            bitmap.Save(tempPath);
            var dataTransfer = new DataTransfer();
            dataTransfer.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));
            var storageFile = await TopLevel.GetTopLevel(this)?.StorageProvider.TryGetFileFromPathAsync(tempPath);
            if (storageFile != null)
                dataTransfer.Add(DataTransferItem.CreateFile(storageFile));
            await clipboard.SetDataAsync(dataTransfer);

        }
        catch
        {
            // 忽略复制失败的情况
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (Source is { } i && _image is { })
        {
            Size size = i.Size;
            double width = Bounds.Width;
            double height = Bounds.Height;
            _image.Width = size.Width;
            _image.Height = size.Height;
            Scale = GetScaleRatio(width / size.Width, height / size.Height, this.Stretch);
            _sourceMinScale = Math.Max(MinScale, Math.Min(width * MinScale / size.Width, height * MinScale / size.Height));
            _sourceMaxScale = Math.Max(MaxScale, _sourceMinScale);
            Scale = Math.Clamp(Scale, _sourceMinScale, _sourceMaxScale);
        }
        else
        {
            _sourceMinScale = MinScale;
            _sourceMaxScale = Math.Max(MaxScale, _sourceMinScale);
        }
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_image == null)
            return;

        if (!ShowZoomToolbar)
            return;

        if (!IsDragEnabled && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        var oldScale = Scale;
        double newScale = e.Delta.Y > 0 ? oldScale * 1.1 : oldScale / 1.1;
        newScale = Math.Clamp(newScale, _sourceMinScale, _sourceMaxScale);
        newScale = Math.Round(newScale, 6); // 精度修正
        var imgP = e.GetPosition(_image);
        Scale = newScale;
        var imgPA = e.GetPosition(_image);
        // 应用边界限制
        ApplyClampedTranslation(
            TranslateX + (imgPA.X - imgP.X) * Scale,
            TranslateY + (imgPA.Y - imgP.Y) * Scale);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsDragEnabled && !_ctrlDragActive)
            return;
        if (Equals(e.Pointer.Captured, this) && _lastClickPoint != null)
        {
            PseudoClasses.Set(PC_Moving, true);
            Point p = e.GetPosition(this);
            double deltaX = p.X - _lastClickPoint.Value.X;
            double deltaY = p.Y - _lastClickPoint.Value.Y;
            // 应用边界限制
            ApplyClampedTranslation(
                deltaX + (_lastLocation?.X ?? 0),
                deltaY + (_lastLocation?.Y ?? 0));
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (!IsDragEnabled)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
                return;
            _ctrlDragActive = point.Properties.IsLeftButtonPressed;
            if (!_ctrlDragActive)
                return;
        }

        e.Pointer.Capture(this);
        Point p = e.GetPosition(_image);
        _lastClickPoint = e.GetPosition(this);
        _moving = true;
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!Equals(e.Pointer.Captured, this))
            return;

        if (!IsDragEnabled && !_ctrlDragActive)
            return;
        e.Pointer.Capture(null);
        _lastLocation = new Point(TranslateX, TranslateY);
        PseudoClasses.Set(PC_Moving, false);
        _moving = false;
        _ctrlDragActive = false;
        e.Handled = true;
    }

// 在 OnSourceChanged 末尾调用居中
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsDragEnabled)
        {
            base.OnKeyDown(e);
            return;
        }

        double step = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? LargeChange : SmallChange;
        double newTranslateX = TranslateX;
        double newTranslateY = TranslateY;
        switch (e.Key)
        {
            case Key.Left:
                newTranslateX -= step;
                break;
            case Key.Right:
                newTranslateX += step;
                break;
            case Key.Up:
                newTranslateY -= step;
                break;
            case Key.Down:
                newTranslateY += step;
                break;
        }
        // 应用边界限制
        ApplyClampedTranslation(newTranslateX, newTranslateY);
        base.OnKeyDown(e);
    }

    public void DrawSelectionRect(Rect rect)
    {
        SelectionRect = rect;
    }

    private void ZoomIn()
    {
        var newScale = Math.Clamp(Scale * 1.1, _sourceMinScale, _sourceMaxScale);
        Scale = Math.Round(newScale, 6);
        ApplyClampedTranslation(TranslateX, TranslateY);
    }

    private void ZoomOut()
    {
        var newScale = Math.Clamp(Scale / 1.1, _sourceMinScale, _sourceMaxScale);
        Scale = Math.Round(newScale, 6);
        ApplyClampedTranslation(TranslateX, TranslateY);
    }

    public void DrawSelectionRects(IEnumerable<Rect> rects)
    {
        SelectionRects ??= new AvaloniaList<Rect>();
        SelectionRects.Clear();
        foreach (var rect in rects)
        {
            if (rect.Width > 0 && rect.Height > 0)
            {
                SelectionRects.Add(rect);
            }
        }

        HasSelection = SelectionRects.Count > 0;
        if (SelectionRects.Count == 1)
        {
            SelectionRect = SelectionRects[0];
        }
    }

    public Rect GetSelectionRect() => SelectionRect;

    public IReadOnlyList<Rect> GetSelectionRects() => SelectionRects;

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
