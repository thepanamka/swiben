using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VinylNowPlaying
{
    public class MainWindow : Window
    {
        // Win32 Constants for Non-Activating ToolWindow
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // UI Elements
        private Grid _rootContainer;
        private TranslateTransform _slideTransform;
        private Border _capsuleCard;
        private Grid _vinylDisc;
        private RotateTransform _vinylRotation;
        private Storyboard _rotationStoryboard;
        private Ellipse _coverLabel;
        private ImageBrush _coverBrush;
        private TextBlock _txtTitle;
        private TextBlock _txtArtist;
        private TextBlock _txtTime;
        private Border _progressBarTrack;
        private Border _progressBarFill;

        // ONLY 3 BUTTONS: Назад, Пауза/Плей, Вперед
        private Button _btnPrev;
        private Button _btnPlay;
        private Button _btnNext;

        // Fallback default label
        private VisualBrush _defaultCoverBrush;
        private BitmapImage _vinylBaseTexture;

        // Animation state
        private bool _isFlownIn = false;
        private DispatcherTimer _pollTimer;

        private MediaManager _media;

        public MainWindow(MediaManager media)
        {
            _media = media;

            InitializeWindowProperties();
            CreateDefaultAssets();
            BuildUI();

            _pollTimer = new DispatcherTimer();
            _pollTimer.Interval = TimeSpan.FromMilliseconds(500);
            _pollTimer.Tick += async (s, e) =>
            {
                await _media.UpdateAsync();
            };

            Loaded += OnLoaded;
        }

        private void InitializeWindowProperties()
        {
            Title = "Vinyl Now Playing";
            Width = 350;
            Height = 96;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;

            // Place in bottom right corner above taskbar
            Rect workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 20;
            Top = workArea.Bottom - Height - 20;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private void CreateDefaultAssets()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream("VinylWidget.vinyl_base.png"))
                {
                    if (stream != null)
                    {
                        _vinylBaseTexture = new BitmapImage();
                        _vinylBaseTexture.BeginInit();
                        _vinylBaseTexture.CacheOption = BitmapCacheOption.OnLoad;
                        _vinylBaseTexture.StreamSource = stream;
                        _vinylBaseTexture.EndInit();
                        _vinylBaseTexture.Freeze();
                    }
                }
            }
            catch { }

            // Default retro crimson album art
            Grid defGrid = new Grid { Width = 48, Height = 48 };
            Ellipse bg = new Ellipse
            {
                Width = 48,
                Height = 48,
                Fill = new RadialGradientBrush(Color.FromRgb(220, 42, 58), Color.FromRgb(115, 15, 25))
            };
            TextBlock note = new TextBlock
            {
                Text = "♫",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            defGrid.Children.Add(bg);
            defGrid.Children.Add(note);
            _defaultCoverBrush = new VisualBrush(defGrid) { Stretch = Stretch.UniformToFill };
        }

        private void BuildUI()
        {
            _slideTransform = new TranslateTransform(420, 0); // Start completely off-screen right

            _rootContainer = new Grid
            {
                Width = 350,
                Height = 96,
                Background = Brushes.Transparent,
                RenderTransform = _slideTransform,
                ClipToBounds = false
            };

            // Allow dragging anywhere on screen
            _rootContainer.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };

            // Double click resets position to dock
            _rootContainer.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                    ResetPosition();
            };

            _rootContainer.ContextMenu = CreateContextMenu();

            // 1. THE CAPSULE CARD (Dark rounded rectangle)
            // Starts at Left: 48px, vinyl disc at Left: 8px -> vinyl protrudes ~40px outside to the left!
            _capsuleCard = new Border
            {
                Width = 280,
                Height = 74,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(48, 0, 0, 0),
                CornerRadius = new CornerRadius(24),
                Background = new LinearGradientBrush(
                    Color.FromRgb(20, 20, 24),
                    Color.FromRgb(11, 11, 13),
                    new Point(0, 0),
                    new Point(1, 1)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(46, 46, 56)),
                BorderThickness = new Thickness(1.2),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 4,
                    Direction = 315,
                    Opacity = 0.85
                }
            };

            Grid metaGrid = new Grid
            {
                Margin = new Thickness(44, 7, 12, 7)
            };
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 0: Title + 3 Buttons
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 1: Artist
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Spacer
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 3: Progress bar + Time

            // Top Row: Title on Left + 3 Buttons (Назад, Пауза/Плей, Вперед) on Right
            Grid topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txtTitle = new TextBlock
            {
                Text = "Ожидание трека...",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 130
            };
            Grid.SetColumn(_txtTitle, 0);
            topRow.Children.Add(_txtTitle);

            // Button Container with ONLY 3 buttons
            StackPanel btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };

            _btnPrev = CreateMediaButton("⏮", "Предыдущий трек", async (s, e) => {
                e.Handled = true;
                await _media.SkipPreviousAsync();
            });

            _btnPlay = CreateMediaButton("⏸", "Пауза / Воспроизведение", async (s, e) => {
                e.Handled = true;
                await _media.TogglePlayPauseAsync();
            });

            _btnNext = CreateMediaButton("⏭", "Следующий трек", async (s, e) => {
                e.Handled = true;
                await _media.SkipNextAsync();
            });

            btnPanel.Children.Add(_btnPrev);
            btnPanel.Children.Add(_btnPlay);
            btnPanel.Children.Add(_btnNext);

            Grid.SetColumn(btnPanel, 1);
            topRow.Children.Add(btnPanel);

            Grid.SetRow(topRow, 0);
            metaGrid.Children.Add(topRow);

            // Artist
            _txtArtist = new TextBlock
            {
                Text = "Запустите музыку",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 172)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
                MaxWidth = 200
            };
            Grid.SetRow(_txtArtist, 1);
            metaGrid.Children.Add(_txtArtist);

            // Progress Bar & Time
            Grid progContainer = new Grid
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 1)
            };
            progContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            progContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid barTrack = new Grid { Height = 3.5 };
            _progressBarTrack = new Border
            {
                Height = 3.5,
                Background = new SolidColorBrush(Color.FromRgb(36, 36, 44)),
                CornerRadius = new CornerRadius(1.75)
            };
            barTrack.Children.Add(_progressBarTrack);

            _progressBarFill = new Border
            {
                Height = 3.5,
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new LinearGradientBrush(
                    Color.FromRgb(255, 255, 255),
                    Color.FromRgb(200, 205, 215),
                    new Point(0, 0),
                    new Point(1, 0)),
                CornerRadius = new CornerRadius(1.75)
            };
            barTrack.Children.Add(_progressBarFill);

            Grid.SetRow(barTrack, 0);
            progContainer.Children.Add(barTrack);

            _txtTime = new TextBlock
            {
                Text = "00:00 / 00:00",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 122)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(_txtTime, 1);
            progContainer.Children.Add(_txtTime);

            Grid.SetRow(progContainer, 3);
            metaGrid.Children.Add(progContainer);

            _capsuleCard.Child = metaGrid;
            _rootContainer.Children.Add(_capsuleCard);

            // 2. VINYL RECORD (Left, PROTRUDING OUTSIDE the capsule as sketched!)
            _vinylDisc = new Grid
            {
                Width = 80,
                Height = 80,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                Cursor = Cursors.Hand,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 14,
                    ShadowDepth = 3,
                    Direction = 315,
                    Opacity = 0.9
                }
            };
            _vinylRotation = new RotateTransform(0);
            _vinylDisc.RenderTransform = _vinylRotation;

            _vinylDisc.MouseLeftButtonUp += async (s, e) =>
            {
                e.Handled = true;
                await _media.TogglePlayPauseAsync();
            };

            if (_vinylBaseTexture != null)
            {
                Image vinylImg = new Image
                {
                    Source = _vinylBaseTexture,
                    Width = 80,
                    Height = 80,
                    Stretch = Stretch.UniformToFill
                };
                _vinylDisc.Children.Add(vinylImg);
            }
            else
            {
                Ellipse disc = new Ellipse
                {
                    Width = 80,
                    Height = 80,
                    Fill = new RadialGradientBrush(Color.FromRgb(28, 28, 32), Color.FromRgb(12, 12, 14)),
                    Stroke = new SolidColorBrush(Color.FromRgb(55, 55, 62)),
                    StrokeThickness = 1.2
                };
                _vinylDisc.Children.Add(disc);
            }

            // Center Label (Album Art Cover)
            _coverBrush = new ImageBrush { Stretch = Stretch.UniformToFill };
            _coverLabel = new Ellipse
            {
                Width = 36,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = _defaultCoverBrush,
                Stroke = new SolidColorBrush(Color.FromRgb(24, 24, 28)),
                StrokeThickness = 1.2
            };
            _vinylDisc.Children.Add(_coverLabel);

            // Spindle Center Hole
            Ellipse spindle = new Ellipse
            {
                Width = 8,
                Height = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Color.FromRgb(12, 12, 14)),
                Stroke = new SolidColorBrush(Color.FromRgb(85, 85, 95)),
                StrokeThickness = 1
            };
            _vinylDisc.Children.Add(spindle);

            _rootContainer.Children.Add(_vinylDisc);

            Content = _rootContainer;

            SetupVinylAnimation();
        }

        private Button CreateMediaButton(string symbol, string tooltip, RoutedEventHandler onClick)
        {
            Button btn = new Button
            {
                Content = symbol,
                ToolTip = tooltip,
                Width = 22,
                Height = 22,
                Margin = new Thickness(2, 0, 2, 0),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 230)),
                Background = new SolidColorBrush(Color.FromArgb(95, 36, 36, 44)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btn.Click += onClick;
            return btn;
        }

        private ContextMenu CreateContextMenu()
        {
            ContextMenu menu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 60)),
                BorderThickness = new Thickness(1)
            };

            MenuItem miPlay = new MenuItem { Header = "⏯ Воспроизведение / Пауза", Foreground = Brushes.White };
            miPlay.Click += async (s, e) => await _media.TogglePlayPauseAsync();
            menu.Items.Add(miPlay);

            MenuItem miNext = new MenuItem { Header = "⏭ Следующий трек", Foreground = Brushes.White };
            miNext.Click += async (s, e) => await _media.SkipNextAsync();
            menu.Items.Add(miNext);

            MenuItem miPrev = new MenuItem { Header = "⏮ Предыдущий трек", Foreground = Brushes.White };
            miPrev.Click += async (s, e) => await _media.SkipPreviousAsync();
            menu.Items.Add(miPrev);

            menu.Items.Add(new Separator { Background = new SolidColorBrush(Color.FromRgb(45, 45, 52)) });

            MenuItem miTest = new MenuItem { Header = "🎬 Тест анимации (Демо трек)", Foreground = Brushes.White };
            miTest.Click += (s, e) => TriggerTestAnimation();
            menu.Items.Add(miTest);

            MenuItem miReset = new MenuItem { Header = "📍 Сбросить позицию (в угол)", Foreground = Brushes.White };
            miReset.Click += (s, e) => ResetPosition();
            menu.Items.Add(miReset);

            menu.Items.Add(new Separator { Background = new SolidColorBrush(Color.FromRgb(45, 45, 52)) });

            MenuItem miExit = new MenuItem { Header = "❌ Выход", Foreground = new SolidColorBrush(Color.FromRgb(240, 100, 100)) };
            miExit.Click += (s, e) => Application.Current.Shutdown();
            menu.Items.Add(miExit);

            return menu;
        }

        private void SetupVinylAnimation()
        {
            _rotationStoryboard = new Storyboard();
            DoubleAnimation spin = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(3.0),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(spin, _vinylDisc);
            Storyboard.SetTargetProperty(spin, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
            _rotationStoryboard.Children.Add(spin);
        }

        private void StartSpinning()
        {
            _rotationStoryboard.Begin(this, true);
        }

        private void PauseSpinning()
        {
            try
            {
                _rotationStoryboard.Pause(this);
            }
            catch { }
        }

        private void ResumeSpinning()
        {
            try
            {
                _rotationStoryboard.Resume(this);
            }
            catch { }
        }

        public void FlyIn()
        {
            if (_isFlownIn) return;
            _isFlownIn = true;

            DoubleAnimation slide = new DoubleAnimation
            {
                From = 420,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(500),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            _slideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
            StartSpinning();
        }

        public void FlyOut()
        {
            if (!_isFlownIn) return;
            _isFlownIn = false;

            DoubleAnimation slide = new DoubleAnimation
            {
                From = 0,
                To = 420,
                Duration = TimeSpan.FromMilliseconds(420),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            slide.Completed += (s, e) =>
            {
                PauseSpinning();
            };
            _slideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        public void ToggleVisibility()
        {
            if (_isFlownIn)
                FlyOut();
            else
                FlyIn();
        }

        public void ResetPosition()
        {
            Rect workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 20;
            Top = workArea.Bottom - Height - 20;
        }

        public void SetTrackInfo(TrackInfo info)
        {
            Dispatcher.Invoke(() =>
            {
                _txtTitle.Text = string.IsNullOrEmpty(info.Title) ? "Неизвестный трек" : info.Title;
                _txtArtist.Text = string.IsNullOrEmpty(info.Artist) ? "Неизвестный автор" : info.Artist;

                // Update Cover Art
                if (info.ThumbnailBytes != null && info.ThumbnailBytes.Length > 0)
                {
                    try
                    {
                        using (MemoryStream ms = new MemoryStream(info.ThumbnailBytes))
                        {
                            BitmapImage bi = new BitmapImage();
                            bi.BeginInit();
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.StreamSource = ms;
                            bi.EndInit();
                            bi.Freeze();
                            _coverBrush.ImageSource = bi;
                            _coverLabel.Fill = _coverBrush;
                        }
                    }
                    catch
                    {
                        _coverLabel.Fill = _defaultCoverBrush;
                    }
                }
                else
                {
                    _coverLabel.Fill = _defaultCoverBrush;
                }

                // ВЫЕЗД ПЛАШКИ ТОЛЬКО ПРИ МУЗЫКЕ!
                if (info.IsPlaying)
                {
                    _btnPlay.Content = "⏸";
                    FlyIn();
                    ResumeSpinning();
                }
                else
                {
                    _btnPlay.Content = "▶";
                    FlyOut();
                    PauseSpinning();
                }
            });
        }

        public void SetPlaybackState(bool isPlaying)
        {
            Dispatcher.Invoke(() =>
            {
                // ВЫЕЗД ПЛАШКИ ТОЛЬКО ПРИ МУЗЫКЕ!
                if (isPlaying)
                {
                    _btnPlay.Content = "⏸";
                    FlyIn();
                    ResumeSpinning();
                }
                else
                {
                    _btnPlay.Content = "▶";
                    FlyOut();
                    PauseSpinning();
                }
            });
        }

        public void SetTimeline(TimeSpan pos, TimeSpan dur)
        {
            Dispatcher.Invoke(() =>
            {
                if (dur.TotalSeconds > 0)
                {
                    _txtTime.Text = string.Format("{0:mm\\:ss} / {1:mm\\:ss}", pos, dur);
                    double maxWidth = 180;
                    double fraction = Math.Max(0, Math.Min(1, pos.TotalSeconds / dur.TotalSeconds));
                    _progressBarFill.Width = maxWidth * fraction;
                }
                else
                {
                    _txtTime.Text = "";
                    _progressBarFill.Width = 0;
                }
            });
        }

        public void TriggerTestAnimation()
        {
            TrackInfo demo = new TrackInfo
            {
                Title = "NaNaNa",
                Artist = "Aarne, Toxi$, OG Buda",
                Album = "AA LANGUAGE 2",
                SourceApp = "Яндекс Музыка",
                IsPlaying = true,
                Position = TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(14)),
                Duration = TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(55))
            };
            SetTrackInfo(demo);
            SetTimeline(demo.Position, demo.Duration);
            FlyIn();
            ResumeSpinning();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await _media.InitializeAsync();
            _media.TrackChanged += SetTrackInfo;
            _media.PlaybackStateChanged += SetPlaybackState;
            _media.TimelineUpdated += SetTimeline;

            _pollTimer.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            _pollTimer.Stop();
            base.OnClosed(e);
        }
    }
}
