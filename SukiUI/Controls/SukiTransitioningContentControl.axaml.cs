using System;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace SukiUI.Controls
{
    // TODO: This needs fairly significant work to make a bit more bomb proof
    // There are probably some more gains that can be made in terms of performance.
    // Unfortunately we're still bound by the arrange of controls having to happen on the main thread.
    public class SukiTransitioningContentControl : TemplatedControl
    {
        internal static readonly StyledProperty<object?> FirstBufferProperty =
            AvaloniaProperty.Register<SukiTransitioningContentControl, object?>(nameof(FirstBuffer));

        internal object? FirstBuffer
        {
            get => GetValue(FirstBufferProperty);
            set => SetValue(FirstBufferProperty, value);
        }

        internal static readonly StyledProperty<object?> SecondBufferProperty =
            AvaloniaProperty.Register<SukiTransitioningContentControl, object?>(nameof(SecondBuffer));

        internal object? SecondBuffer
        {
            get => GetValue(SecondBufferProperty);
            set => SetValue(SecondBufferProperty, value);
        }

        public static readonly StyledProperty<object?> ContentProperty = AvaloniaProperty.Register<SukiTransitioningContentControl, object?>(nameof(Content));

        public object? Content
        {
            get => GetValue(ContentProperty);
            set => SetValue(ContentProperty, value);
        }

        private bool _isFirstBufferActive;

        private ContentPresenter? _firstBuffer = null;
        private ContentPresenter? _secondBuffer = null;

        private static readonly Animation FadeIn;
        private static readonly Animation FadeOut;
        
        private ContentPresenter? To => _isFirstBufferActive ? _firstBuffer : _secondBuffer;
        private ContentPresenter? From => _isFirstBufferActive ? _secondBuffer : _firstBuffer;

        private object? _contentBeforeApplied;

        static SukiTransitioningContentControl()
        {
            FadeIn = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(400),
                Children =
                {
                    new KeyFrame()
                    {
                        Setters =
                        {
                            new Setter
                            {
                                Property = OpacityProperty,
                                Value = 0d
                            }
                        },
                        Cue = new Cue(0d)
                    },
                    new KeyFrame()
                    {
                        Setters =
                        {
                            new Setter
                            {
                                Property = OpacityProperty,
                                Value = 1d
                            }
                        },
                        Cue = new Cue(1d)
                    }
                },
                FillMode = FillMode.Forward
            };
            FadeOut = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(400),
                Children =
                {
                    new KeyFrame()
                    {
                        Setters =
                        {
                            new Setter
                            {
                                Property = OpacityProperty,
                                Value = 1d
                            }
                        },
                        Cue = new Cue(0d)
                    },
                    new KeyFrame()
                    {
                        Setters =
                        {
                            new Setter
                            {
                                Property = OpacityProperty,
                                Value = 0d
                            }
                        },
                        Cue = new Cue(1d)
                    }
                },
                FillMode = FillMode.Forward
            };
            FadeIn.Duration = FadeOut.Duration = TimeSpan.FromMilliseconds(250);
        }

        private CancellationTokenSource _animCancellationToken = new();
        

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if(change.Property == ContentProperty)
                PushContent(change.NewValue);
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            if (e.NameScope.Get<ContentPresenter>("PART_FirstBufferControl") is { } fBuff)
                _firstBuffer = fBuff;
            if (e.NameScope.Get<ContentPresenter>("PART_SecondBufferControl") is { } sBuff)
                _secondBuffer = sBuff;
            if (_contentBeforeApplied != null)
            {
                PushContent(_contentBeforeApplied);
                _contentBeforeApplied = null;
            }
        }

        private void PushContent(object? content)
        {
            if (To is null || From is null)
            {
                _contentBeforeApplied = content;
                return;
            }

            try
            {
                _animCancellationToken.Cancel();
                _animCancellationToken.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            _animCancellationToken = new CancellationTokenSource();
            var token = _animCancellationToken.Token;
            
            if (_isFirstBufferActive) SecondBuffer = content;
            else FirstBuffer = content;
            _isFirstBufferActive = !_isFirstBufferActive;

            var toPresenter = To;
            var fromPresenter = From;

            if (toPresenter is null || fromPresenter is null)
                return;

            var clearFirstBuffer = ReferenceEquals(fromPresenter, _firstBuffer);

            toPresenter.IsHitTestVisible = false;
            fromPresenter.IsHitTestVisible = false;

            try
            {
                FadeOut.RunAsync(fromPresenter, token).ContinueWith(_ =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        fromPresenter.IsHitTestVisible = false;
                        if (clearFirstBuffer) FirstBuffer = null;
                        else SecondBuffer = null;
                    });
                });
                FadeIn.RunAsync(toPresenter, token).ContinueWith(_ => 
                {
                    if (token.IsCancellationRequested)
                        return;

                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        toPresenter.IsHitTestVisible = true;
                    });
                });
            }
            catch
            {
                // ignored
            }
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);
            _animCancellationToken.Dispose();
        }
    }
}
