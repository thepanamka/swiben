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
        private Border _rootCard;
        private TranslateTransform _slideTransform;
        private Grid _vinylDisc;
        private RotateTransform _vinylRotation;
        private Storyboard _rotationStoryboard;
        private Ellipse _coverLabel;
        private ImageBrush _coverBrush;
        private TextBlock _txtTitle;
        private TextBlock _txtArtist;
        private TextBlock _txtAppBadge;
        private TextBlock _txtTime;
        private Border _progressBarTrack;
        private Border _progressBarFill;
        private StackPanel _controlsPanel;

        // Fallback default label
        private VisualBrush _defaultCoverBrush;
        private BitmapImage _vinylBaseTexture;

        // Animation state
        private bool _isFlownIn = false;
        private DispatcherTimer _autoHideTimer;
        private DispatcherTimer _pollTimer;

        // Settings
        public bool AlwaysVisibleWhilePlaying { get; set; }
        private MediaManager _media;

        public MainWindow(MediaManager media)
        {
            _media = media;
            AlwaysVisibleWhilePlaying = true;

            InitializeWindowProperties();
            CreateDefaultAssets();
            BuildUI();

            _autoHideTimer = new DispatcherTimer();
            _autoHideTimer.Interval = TimeSpan.FromSeconds(7);
            _autoHideTimer.Tick += (s, e) =>
            {
                _autoHideTimer.Stop();
                if (!AlwaysVisibleWhilePlaying && !IsMouseOver)
                {
                    FlyOut();
                }
            };

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
            Width = 430;
            Height = 120;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;

            // Place in bottom right corner above taskbar
            Rect workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 24;
            Top = workArea.Bottom - Height - 24;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Apply ToolWindow and NoActivate styles
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private void CreateDefaultAssets()
        {
            // Load embedded vinyl base texture
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

            // Default fallback album art (retro crimson vinyl label)
            Grid defGrid = new Grid { Width = 64, Height = 64 };
            Ellipse bg = new Ellipse
            {
                Width = 64,
                Height = 64,
                Fill = new RadialGradientBrush(Color.FromRgb(215, 38, 56), Color.FromRgb(110, 15, 25))
            };
            TextBlock note = new TextBlock
            {
                Text = "♫",
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            defGrid.Children.Add(bg);
            defGrid.Children.Add(note);
            _defaultCoverBrush = new VisualBrush(defGrid) { Stretch = Stretch.UniformToFill };
        }

        private void BuildUI()
        {
            _slideTransform = new TranslateTransform(480, 0); // Start off-screen right

            _rootCard = new Border
            {
                Width = 420,
                Height = 110,
                CornerRadius = new CornerRadius(36),
                Background = new LinearGradientBrush(
                    Color.FromRgb(20, 20, 23),
                    Color.FromRgb(11, 11, 13),
                    new Point(0, 0),
                    new Point(1, 1)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 52)),
                BorderThickness = new Thickness(1.2),
                RenderTransform = _slideTransform,
                ClipToBounds = false,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 24,
                    ShadowDepth = 5,
                    Direction = 315,
                    Opacity = 0.82
                }
            };

            // Allow dragging window anywhere on desktop
            _rootCard.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };

            // Double click resets position to dock
            _rootCard.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                    ResetPosition();
            };

            _rootCard.ContextMenu = CreateContextMenu();

            Grid layoutGrid = new Grid();

            // 1. VINYL RECORD (Left)
            _vinylDisc = new Grid
            {
                Width = 96,
                Height = 96,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                Cursor = Cursors.Hand
            };
            _vinylRotation = new RotateTransform(0);
            _vinylDisc.RenderTransform = _vinylRotation;

            // Click vinyl to toggle play/pause
            _vinylDisc.MouseLeftButtonUp += async (s, e) =>
            {
                e.Handled = true;
                await _media.TogglePlayPauseAsync();
            };

            // Vinyl Base Texture
            if (_vinylBaseTexture != null)
            {
                Image vinylImg = new Image
                {
                    Source = _vinylBaseTexture,
                    Width = 96,
                    Height = 96,
                    Stretch = Stretch.UniformToFill
                };
                _vinylDisc.Children.Add(vinylImg);
            }
            else
            {
                // Procedural disc fallback
                Ellipse disc = new Ellipse
                {
                    Width = 96,
                    Height = 96,
                    Fill = new RadialGradientBrush(Color.FromRgb(30, 30, 34), Color.FromRgb(14, 14, 16)),
                    Stroke = new SolidColorBrush(Color.FromRgb(60, 60, 68)),
                    StrokeThickness = 1.5
                };
                _vinylDisc.Children.Add(disc);
            }

            // Center Label (Album Art Cover)
            _coverBrush = new ImageBrush { Stretch = Stretch.UniformToFill };
            _coverLabel = new Ellipse
            {
                Width = 44,
                Height = 44,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = _defaultCoverBrush,
                Stroke = new SolidColorBrush(Color.FromRgb(28, 28, 32)),
                StrokeThickness = 1.5
            };
            _vinylDisc.Children.Add(_coverLabel);

            // Spindle Center Hole
            Ellipse spindleRing = new Ellipse
            {
                Width = 10,
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Color.FromRgb(12, 12, 14)),
                Stroke = new SolidColorBrush(Color.FromRgb(85, 85, 95)),
                StrokeThickness = 1.2
            };
            _vinylDisc.Children.Add(spindleRing);

            layoutGrid.Children.Add(_vinylDisc);

            // 2. METADATA & PROGRESS (Right)
            Grid metaGrid = new Grid
            {
                Margin = new Thickness(118, 12, 16, 12)
            };
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header (App badge + time)
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Title
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Artist
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Spacer
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Progress bar

            // Header row
            Grid headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txtAppBadge = new TextBlock
            {
                Text = "МУЗЫКА",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 150)),
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            Border badgeBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                Child = _txtAppBadge
            };
            Grid.SetColumn(badgeBorder, 0);
            headerRow.Children.Add(badgeBorder);

            _txtTime = new TextBlock
            {
                Text = "00:00 / 00:00",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(115, 115, 125)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_txtTime, 2);
            headerRow.Children.Add(_txtTime);

            Grid.SetRow(headerRow, 0);
            metaGrid.Children.Add(headerRow);

            // Title
            _txtTitle = new TextBlock
            {
                Text = "Ожидание трека...",
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            };
            Grid.SetRow(_txtTitle, 1);
            metaGrid.Children.Add(_txtTitle);

            // Artist
            _txtArtist = new TextBlock
            {
                Text = "Запустите музыку в любом плеере",
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 175)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            };
            Grid.SetRow(_txtArtist, 2);
            metaGrid.Children.Add(_txtArtist);

            // Progress Bar
            Grid progressContainer = new Grid
            {
                Height = 5,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2)
            };
            _progressBarTrack = new Border
            {
                Height = 4,
                Background = new SolidColorBrush(Color.FromRgb(36, 36, 42)),
                CornerRadius = new CornerRadius(2)
            };
            progressContainer.Children.Add(_progressBarTrack);

            _progressBarFill = new Border
            {
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
                Background = new LinearGradientBrush(
                    Color.FromRgb(255, 255, 255),
                    Color.FromRgb(200, 205, 215),
                    new Point(0, 0),
                    new Point(1, 0)),
                CornerRadius = new CornerRadius(2)
            };
            progressContainer.Children.Add(_progressBarFill);

            Grid.SetRow(progressContainer, 4);
            metaGrid.Children.Add(progressContainer);

            layoutGrid.Children.Add(metaGrid);

            // 3. QUICK CONTROLS OVERLAY (Fade in on hover)
            _controlsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 8, 0),
                Opacity = 0
            };

            Button btnPrev = CreateHoverButton("⏮", "Предыдущий трек", async (s, e) => await _media.SkipPreviousAsync());
            Button btnPlay = CreateHoverButton("⏯", "Пауза/Воспроизведение", async (s, e) => await _media.TogglePlayPauseAsync());
            Button btnNext = CreateHoverButton("⏭", "Следующий трек", async (s, e) => await _media.SkipNextAsync());
            Button btnClose = CreateHoverButton("✕", "Скрыть оверлей", (s, e) => FlyOut());

            _controlsPanel.Children.Add(btnPrev);
            _controlsPanel.Children.Add(btnPlay);
            _controlsPanel.Children.Add(btnNext);
            _controlsPanel.Children.Add(btnClose);

            layoutGrid.Children.Add(_controlsPanel);

            // Hover fade-in for controls
            _rootCard.MouseEnter += (s, e) =>
            {
                DoubleAnimation fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200));
                _controlsPanel.BeginAnimation(UIElement.OpacityProperty, fade);
                _autoHideTimer.Stop(); // Don't auto-hide while user is interacting
            };
            _rootCard.MouseLeave += (s, e) =>
            {
                DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
                _controlsPanel.BeginAnimation(UIElement.OpacityProperty, fade);
                if (!AlwaysVisibleWhilePlaying && _isFlownIn)
                {
                    _autoHideTimer.Start();
                }
            };

            _rootCard.Child = layoutGrid;
            Content = _rootCard;

            // Setup continuous clockwise spinning animation
            SetupVinylAnimation();
        }

        private Button CreateHoverButton(string symbol, string tooltip, RoutedEventHandler onClick)
        {
            Button btn = new Button
            {
                Content = symbol,
                ToolTip = tooltip,
                Width = 24,
                Height = 22,
                Margin = new Thickness(2, 0, 2, 0),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 200)),
                Background = new SolidColorBrush(Color.FromArgb(80, 25, 25, 30)),
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

            MenuItem miAlways = new MenuItem
            {
                Header = "📌 Всегда виден при воспроизведении",
                IsCheckable = true,
                IsChecked = AlwaysVisibleWhilePlaying,
                Foreground = Brushes.White
            };
            miAlways.Click += (s, e) =>
            {
                AlwaysVisibleWhilePlaying = miAlways.IsChecked;
            };
            menu.Items.Add(miAlways);

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
                Duration = TimeSpan.FromSeconds(3.2),
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
                From = 480,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(550),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            _slideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
            StartSpinning();

            if (!AlwaysVisibleWhilePlaying)
            {
                _autoHideTimer.Stop();
                _autoHideTimer.Start();
            }
        }

        public void FlyOut()
        {
            if (!_isFlownIn) return;
            _isFlownIn = false;

            DoubleAnimation slide = new DoubleAnimation
            {
                From = 0,
                To = 480,
                Duration = TimeSpan.FromMilliseconds(450),
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
            Left = workArea.Right - Width - 24;
            Top = workArea.Bottom - Height - 24;
        }

        public void SetTrackInfo(TrackInfo info)
        {
            Dispatcher.Invoke(() =>
            {
                _txtTitle.Text = string.IsNullOrEmpty(info.Title) ? "Неизвестный трек" : info.Title;
                _txtArtist.Text = string.IsNullOrEmpty(info.Artist) ? "Неизвестный автор" : info.Artist;
                _txtAppBadge.Text = (info.SourceApp ?? "МУЗЫКА").ToUpperInvariant();

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

                // If music is playing or new track arrived, trigger Fly In!
                FlyIn();
                if (info.IsPlaying)
                {
                    ResumeSpinning();
                }
                else
                {
                    PauseSpinning();
                }
            });
        }

        public void SetPlaybackState(bool isPlaying)
        {
            Dispatcher.Invoke(() =>
            {
                if (isPlaying)
                {
                    FlyIn();
                    ResumeSpinning();
                }
                else
                {
                    PauseSpinning();
                    if (!AlwaysVisibleWhilePlaying)
                    {
                        FlyOut();
                    }
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
                    double maxWidth = 280; // approximate width of track
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
            // Demo Track for immediate testing!
            TrackInfo demo = new TrackInfo
            {
                Title = "Get Lucky (feat. Pharrell Williams)",
                Artist = "Daft Punk",
                Album = "Random Access Memories",
                SourceApp = "Spotify",
                IsPlaying = true,
                Position = TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(24)),
                Duration = TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(8))
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
            _autoHideTimer.Stop();
            base.OnClosed(e);
        }
    }
}
