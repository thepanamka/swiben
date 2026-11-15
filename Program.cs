using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using WpfContextMenu = System.Windows.Controls.ContextMenu;

namespace VinylNowPlaying
{
    // ==========================================
    // 1. DATA MODELS
    // ==========================================
    public class TrackInfo
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public string SourceApp { get; set; }
        public bool IsPlaying { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
        public byte[] ThumbnailBytes { get; set; }

        public string TrackKey
        {
            get { return (Title ?? "") + "___" + (Artist ?? ""); }
        }
    }

    // ==========================================
    // 2. WINDOWS MEDIA SESSION MANAGER (GSMTC)
    // ==========================================
    public class MediaManager
    {
        private object _sessionManager;
        private Type _mgrType;
        private Assembly _rtAsm;
        private MethodInfo _asTaskGeneric;

        private Type _propType;
        private Type _streamType;

        private object _currentSession;
        private string _lastTrackKey = "";
        private bool _lastIsPlaying = false;

        public event Action<TrackInfo> TrackChanged;
        public event Action<bool> PlaybackStateChanged;
        public event Action<TimeSpan, TimeSpan> TimelineUpdated;

        public bool IsInitialized { get; private set; }

        public async Task InitializeAsync()
        {
            try
            {
                _mgrType = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media, ContentType=WindowsRuntime");
                if (_mgrType == null) return;

                _propType = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media, ContentType=WindowsRuntime");
                _streamType = Type.GetType("Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage, ContentType=WindowsRuntime")
                           ?? Type.GetType("Windows.Storage.Streams.IRandomAccessStream, Windows.Storage, ContentType=WindowsRuntime");

                _rtAsm = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                Type extType = _rtAsm.GetType("System.WindowsRuntimeSystemExtensions");

                foreach (var m in extType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "AsTask" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    {
                        _asTaskGeneric = m;
                        break;
                    }
                }

                MethodInfo reqMethod = _mgrType.GetMethod("RequestAsync", BindingFlags.Public | BindingFlags.Static);
                object asyncOp = reqMethod.Invoke(null, null);

                MethodInfo asTask = _asTaskGeneric.MakeGenericMethod(_mgrType);
                Task mgrTask = (Task)asTask.Invoke(null, new object[] { asyncOp });
                await mgrTask.ConfigureAwait(false);

                _sessionManager = mgrTask.GetType().GetProperty("Result").GetValue(mgrTask, null);
                IsInitialized = (_sessionManager != null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("MediaManager Init Error: " + ex.Message);
            }
        }

        public async Task UpdateAsync()
        {
            if (!IsInitialized || _sessionManager == null) return;

            try
            {
                object session = null;
                try
                {
                    MethodInfo getCurSession = _mgrType.GetMethod("GetCurrentSession", BindingFlags.Public | BindingFlags.Instance);
                    session = getCurSession.Invoke(_sessionManager, null);
                }
                catch { }

                try
                {
                    MethodInfo getAllSessions = _mgrType.GetMethod("GetSessions", BindingFlags.Public | BindingFlags.Instance);
                    object allSessionsList = getAllSessions.Invoke(_sessionManager, null);
                    if (allSessionsList is IEnumerable)
                    {
                        object playingSession = null;
                        foreach (object s in (IEnumerable)allSessionsList)
                        {
                            if (s == null) continue;
                            int status = GetSessionPlaybackStatus(s);
                            if (status == 4) // Playing
                            {
                                playingSession = s;
                                break;
                            }
                        }
                        if (playingSession != null)
                        {
                            session = playingSession;
                        }
                        else if (session == null)
                        {
                            IEnumerator en = ((IEnumerable)allSessionsList).GetEnumerator();
                            if (en.MoveNext())
                                session = en.Current;
                        }
                    }
                }
                catch { }

                _currentSession = session;

                if (session == null)
                {
                    if (_lastIsPlaying)
                    {
                        _lastIsPlaying = false;
                        if (PlaybackStateChanged != null)
                            PlaybackStateChanged(false);
                    }
                    return;
                }

                Type sessionType = session.GetType();

                int statusInt = GetSessionPlaybackStatus(session);
                bool isPlaying = (statusInt == 4);

                if (isPlaying != _lastIsPlaying)
                {
                    _lastIsPlaying = isPlaying;
                    if (PlaybackStateChanged != null)
                        PlaybackStateChanged(isPlaying);
                }

                object propOp = sessionType.GetMethod("TryGetMediaPropertiesAsync").Invoke(session, null);
                if (propOp == null) return;

                MethodInfo asTaskProp = _asTaskGeneric.MakeGenericMethod(_propType);
                Task propTask = (Task)asTaskProp.Invoke(null, new object[] { propOp });
                await propTask.ConfigureAwait(false);

                object props = propTask.GetType().GetProperty("Result").GetValue(propTask, null);
                if (props != null)
                {
                    Type pt = props.GetType();
                    string title = pt.GetProperty("Title").GetValue(props, null) as string ?? "";
                    string artist = pt.GetProperty("Artist").GetValue(props, null) as string ?? "";
                    string album = pt.GetProperty("AlbumTitle").GetValue(props, null) as string ?? "";
                    string appId = sessionType.GetProperty("SourceAppUserModelId").GetValue(session, null) as string ?? "";

                    string friendlyApp = CleanAppId(appId);
                    string key = title + "___" + artist;

                    TimeSpan position = TimeSpan.Zero;
                    TimeSpan duration = TimeSpan.Zero;
                    try
                    {
                        MethodInfo getTimeline = sessionType.GetMethod("GetTimelineProperties");
                        object timeline = getTimeline.Invoke(session, null);
                        if (timeline != null)
                        {
                            Type tt = timeline.GetType();
                            position = (TimeSpan)tt.GetProperty("Position").GetValue(timeline, null);
                            duration = (TimeSpan)tt.GetProperty("EndTime").GetValue(timeline, null);
                        }
                    }
                    catch { }

                    if (TimelineUpdated != null)
                        TimelineUpdated(position, duration);

                    if (key != _lastTrackKey && (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(artist)))
                    {
                        _lastTrackKey = key;

                        byte[] thumbBytes = null;
                        try
                        {
                            object thumbRef = pt.GetProperty("Thumbnail").GetValue(props, null);
                            if (thumbRef != null)
                            {
                                thumbBytes = await ExtractThumbnailBytesAsync(thumbRef).ConfigureAwait(false);
                            }
                        }
                        catch { }

                        TrackInfo info = new TrackInfo
                        {
                            Title = string.IsNullOrEmpty(title) ? "Неизвестный трек" : title,
                            Artist = string.IsNullOrEmpty(artist) ? "Неизвестный исполнитель" : artist,
                            Album = album,
                            SourceApp = friendlyApp,
                            IsPlaying = isPlaying,
                            Position = position,
                            Duration = duration,
                            ThumbnailBytes = thumbBytes
                        };

                        if (TrackChanged != null)
                            TrackChanged(info);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("MediaManager Update Error: " + ex);
            }
        }

        private int GetSessionPlaybackStatus(object session)
        {
            if (session == null) return 0;
            try
            {
                MethodInfo getPlayback = session.GetType().GetMethod("GetPlaybackInfo");
                object pb = getPlayback.Invoke(session, null);
                if (pb != null)
                {
                    object statusObj = pb.GetType().GetProperty("PlaybackStatus").GetValue(pb, null);
                    return Convert.ToInt32(statusObj);
                }
            }
            catch { }
            return 0;
        }

        private async Task<byte[]> ExtractThumbnailBytesAsync(object thumbRef)
        {
            if (thumbRef == null || _streamType == null) return null;
            MethodInfo openMethod = thumbRef.GetType().GetMethod("OpenReadAsync");
            if (openMethod == null) return null;

            object asyncOp = openMethod.Invoke(thumbRef, null);
            MethodInfo asTask = _asTaskGeneric.MakeGenericMethod(_streamType);
            Task streamTask = (Task)asTask.Invoke(null, new object[] { asyncOp });
            await streamTask.ConfigureAwait(false);

            object rasStream = streamTask.GetType().GetProperty("Result").GetValue(streamTask, null);
            if (rasStream == null) return null;

            Type extType = _rtAsm.GetType("System.IO.WindowsRuntimeStreamExtensions");
            MethodInfo asStreamMethod = null;
            foreach (var m in extType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name == "AsStreamForRead" && m.GetParameters().Length == 1)
                {
                    asStreamMethod = m;
                    break;
                }
            }
            if (asStreamMethod == null) return null;

            using (Stream dotNetStream = (Stream)asStreamMethod.Invoke(null, new object[] { rasStream }))
            using (MemoryStream ms = new MemoryStream())
            {
                await dotNetStream.CopyToAsync(ms);
                return ms.ToArray();
            }
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_currentSession == null) return;
            try
            {
                MethodInfo m = _currentSession.GetType().GetMethod("TryTogglePlayPauseAsync");
                if (m != null)
                {
                    object op = m.Invoke(_currentSession, null);
                    await AwaitGenericAsync(op).ConfigureAwait(false);
                }
            }
            catch { }
        }

        public async Task SkipNextAsync()
        {
            if (_currentSession == null) return;
            try
            {
                MethodInfo m = _currentSession.GetType().GetMethod("TrySkipNextAsync");
                if (m != null)
                {
                    object op = m.Invoke(_currentSession, null);
                    await AwaitGenericAsync(op).ConfigureAwait(false);
                }
            }
            catch { }
        }

        public async Task SkipPreviousAsync()
        {
            if (_currentSession == null) return;
            try
            {
                MethodInfo m = _currentSession.GetType().GetMethod("TrySkipPreviousAsync");
                if (m != null)
                {
                    object op = m.Invoke(_currentSession, null);
                    await AwaitGenericAsync(op).ConfigureAwait(false);
                }
            }
            catch { }
        }

        private async Task AwaitGenericAsync(object asyncOp)
        {
            if (asyncOp == null || _asTaskGeneric == null) return;
            try
            {
                Type boolType = typeof(bool);
                MethodInfo asTask = _asTaskGeneric.MakeGenericMethod(boolType);
                Task t = (Task)asTask.Invoke(null, new object[] { asyncOp });
                await t.ConfigureAwait(false);
            }
            catch { }
        }

        private string CleanAppId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Музыка";
            string lower = raw.ToLowerInvariant();
            if (lower.Contains("yandex") || lower.Contains("music")) return "Яндекс Музыка";
            if (lower.Contains("spotify")) return "Spotify";
            if (lower.Contains("chrome")) return "Chrome";
            if (lower.Contains("firefox")) return "Firefox";
            if (lower.Contains("msedge") || lower.Contains("edge")) return "Edge";
            if (lower.Contains("opera")) return "Opera";
            if (lower.Contains("telegram")) return "Telegram";
            if (lower.Contains("discord")) return "Discord";
            if (lower.Contains("vlc")) return "VLC";
            if (lower.Contains("aimp")) return "AIMP";
            if (lower.Contains("foobar")) return "Foobar2000";
            if (lower.Contains("apple")) return "Apple Music";
            if (lower.Contains("vk")) return "VK Музыка";

            int lastSlash = Math.Max(raw.LastIndexOf('\\'), raw.LastIndexOf('/'));
            if (lastSlash >= 0 && lastSlash < raw.Length - 1)
                raw = raw.Substring(lastSlash + 1);
            if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(0, raw.Length - 4);

            return raw;
        }
    }

    // ==========================================
    // 3. MAIN OVERLAY WINDOW
    // ==========================================
    public class MainWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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

        private Button _btnPrev;
        private Button _btnPlay;
        private Button _btnNext;

        private VisualBrush _defaultCoverBrush;
        private BitmapImage _vinylBaseTexture;

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
            Background = System.Windows.Media.Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;

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
                Stream stream = asm.GetManifestResourceStream("VinylWidget.vinyl_base.png");
                if (stream == null && File.Exists("assets\\vinyl_base.png"))
                {
                    stream = File.OpenRead("assets\\vinyl_base.png");
                }
                if (stream != null)
                {
                    using (stream)
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

            Grid defGrid = new Grid { Width = 48, Height = 48 };
            Ellipse bg = new Ellipse
            {
                Width = 48,
                Height = 48,
                Fill = new RadialGradientBrush(System.Windows.Media.Color.FromRgb(220, 42, 58), System.Windows.Media.Color.FromRgb(115, 15, 25))
            };
            TextBlock note = new TextBlock
            {
                Text = "♫",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            defGrid.Children.Add(bg);
            defGrid.Children.Add(note);
            _defaultCoverBrush = new VisualBrush(defGrid) { Stretch = Stretch.UniformToFill };
        }

        private void BuildUI()
        {
            _slideTransform = new TranslateTransform(420, 0);

            _rootContainer = new Grid
            {
                Width = 350,
                Height = 96,
                Background = System.Windows.Media.Brushes.Transparent,
                RenderTransform = _slideTransform,
                ClipToBounds = false
            };

            _rootContainer.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };

            _rootContainer.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                    ResetPosition();
            };

            _rootContainer.ContextMenu = CreateContextMenu();

            _capsuleCard = new Border
            {
                Width = 280,
                Height = 74,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(48, 0, 0, 0),
                CornerRadius = new CornerRadius(24),
                Background = new LinearGradientBrush(
                    System.Windows.Media.Color.FromRgb(20, 20, 24),
                    System.Windows.Media.Color.FromRgb(11, 11, 13),
                    new Point(0, 0),
                    new Point(1, 1)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 46, 56)),
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
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txtTitle = new TextBlock
            {
                Text = "Ожидание трека...",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 130
            };
            Grid.SetColumn(_txtTitle, 0);
            topRow.Children.Add(_txtTitle);

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

            _txtArtist = new TextBlock
            {
                Text = "Запустите музыку",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 160, 172)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
                MaxWidth = 200
            };
            Grid.SetRow(_txtArtist, 1);
            metaGrid.Children.Add(_txtArtist);

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
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 36, 44)),
                CornerRadius = new CornerRadius(1.75)
            };
            barTrack.Children.Add(_progressBarTrack);

            _progressBarFill = new Border
            {
                Height = 3.5,
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new LinearGradientBrush(
                    System.Windows.Media.Color.FromRgb(255, 255, 255),
                    System.Windows.Media.Color.FromRgb(200, 205, 215),
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
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(110, 110, 122)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(_txtTime, 1);
            progContainer.Children.Add(_txtTime);

            Grid.SetRow(progContainer, 3);
            metaGrid.Children.Add(progContainer);

            _capsuleCard.Child = metaGrid;
            _rootContainer.Children.Add(_capsuleCard);

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
                System.Windows.Controls.Image vinylImg = new System.Windows.Controls.Image
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
                    Fill = new RadialGradientBrush(System.Windows.Media.Color.FromRgb(28, 28, 32), System.Windows.Media.Color.FromRgb(12, 12, 14)),
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 55, 62)),
                    StrokeThickness = 1.2
                };
                _vinylDisc.Children.Add(disc);
            }

            _coverBrush = new ImageBrush { Stretch = Stretch.UniformToFill };
            _coverLabel = new Ellipse
            {
                Width = 36,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = _defaultCoverBrush,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 28)),
                StrokeThickness = 1.2
            };
            _vinylDisc.Children.Add(_coverLabel);

            Ellipse spindle = new Ellipse
            {
                Width = 8,
                Height = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(12, 12, 14)),
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 85, 95)),
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
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 230)),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(95, 36, 36, 44)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btn.Click += onClick;
            return btn;
        }

        private WpfContextMenu CreateContextMenu()
        {
            WpfContextMenu menu = new WpfContextMenu
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 28)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 50, 60)),
                BorderThickness = new Thickness(1)
            };

            MenuItem miPlay = new MenuItem { Header = "⏯ Воспроизведение / Пауза", Foreground = System.Windows.Media.Brushes.White };
            miPlay.Click += async (s, e) => await _media.TogglePlayPauseAsync();
            menu.Items.Add(miPlay);

            MenuItem miNext = new MenuItem { Header = "⏭ Следующий трек", Foreground = System.Windows.Media.Brushes.White };
            miNext.Click += async (s, e) => await _media.SkipNextAsync();
            menu.Items.Add(miNext);

            MenuItem miPrev = new MenuItem { Header = "⏮ Предыдущий трек", Foreground = System.Windows.Media.Brushes.White };
            miPrev.Click += async (s, e) => await _media.SkipPreviousAsync();
            menu.Items.Add(miPrev);

            menu.Items.Add(new Separator { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 52)) });

            MenuItem miAuto = new MenuItem
            {
                Header = "🚀 Автозагрузка с Windows",
                IsCheckable = true,
                IsChecked = Program.IsAutoStartEnabled(),
                Foreground = System.Windows.Media.Brushes.White
            };
            miAuto.Click += (s, e) =>
            {
                Program.SetAutoStart(miAuto.IsChecked);
            };
            menu.Items.Add(miAuto);

            MenuItem miTest = new MenuItem { Header = "🎬 Тест анимации (Демо трек)", Foreground = System.Windows.Media.Brushes.White };
            miTest.Click += (s, e) => TriggerTestAnimation();
            menu.Items.Add(miTest);

            MenuItem miReset = new MenuItem { Header = "📍 Сбросить позицию (в угол)", Foreground = System.Windows.Media.Brushes.White };
            miReset.Click += (s, e) => ResetPosition();
            menu.Items.Add(miReset);

            menu.Items.Add(new Separator { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 52)) });

            MenuItem miExit = new MenuItem { Header = "❌ Выход", Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 100, 100)) };
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

    // ==========================================
    // 4. PROGRAM ENTRY POINT & SYSTEM TRAY
    // ==========================================
    public class Program : Application
    {
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "VinylNowPlaying";

        private static Mutex _appMutex;
        private NotifyIcon _trayIcon;
        private MainWindow _mainWindow;
        private MediaManager _mediaManager;

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                {
                    return key != null && key.GetValue(APP_NAME) != null;
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        string exePath = Process.GetCurrentProcess().MainModule.FileName;
                        key.SetValue(APP_NAME, "\"" + exePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(APP_NAME, false);
                    }
                }
            }
            catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            bool createdNew;
            _appMutex = new Mutex(true, "VinylNowPlaying_SingleInstance_Mutex_98741", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "Vinyl Now Playing уже запущен!\nИщите значок пластинки в трее Windows (возле часов).",
                    "Vinyl Now Playing",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Ensure auto-start with Windows is enabled by default
            if (!IsAutoStartEnabled())
            {
                SetAutoStart(true);
            }

            Program app = new Program();
            app.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mediaManager = new MediaManager();
            _mainWindow = new MainWindow(_mediaManager);
            _mainWindow.Show();

            SetupTrayIcon();

            if (e.Args.Length > 0 && e.Args[0].Equals("--test", StringComparison.OrdinalIgnoreCase))
            {
                _mainWindow.TriggerTestAnimation();
            }
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new NotifyIcon();

            try
            {
                if (File.Exists("assets\\app.ico"))
                {
                    _trayIcon.Icon = new Icon("assets\\app.ico");
                }
                else if (File.Exists("app.ico"))
                {
                    _trayIcon.Icon = new Icon("app.ico");
                }
                else
                {
                    _trayIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _trayIcon.Icon = SystemIcons.Application;
            }

            _trayIcon.Text = "Vinyl Now Playing - Виджет пластинки";
            _trayIcon.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.BackColor = System.Drawing.Color.FromArgb(28, 28, 32);
            menu.ForeColor = System.Drawing.Color.White;
            menu.ShowImageMargin = false;

            ToolStripMenuItem itemToggle = new ToolStripMenuItem("Показать / Скрыть виджет");
            itemToggle.Click += (s, e) => _mainWindow.ToggleVisibility();
            menu.Items.Add(itemToggle);

            ToolStripMenuItem itemAuto = new ToolStripMenuItem("🚀 Автозагрузка с Windows");
            itemAuto.CheckOnClick = true;
            itemAuto.Checked = IsAutoStartEnabled();
            itemAuto.CheckedChanged += (s, e) => SetAutoStart(itemAuto.Checked);
            menu.Items.Add(itemAuto);

            ToolStripMenuItem itemTest = new ToolStripMenuItem("🎬 Тест анимации (Демо трек)");
            itemTest.Click += (s, e) => _mainWindow.TriggerTestAnimation();
            menu.Items.Add(itemTest);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem itemReset = new ToolStripMenuItem("📍 Сбросить позицию (в угол)");
            itemReset.Click += (s, e) => _mainWindow.ResetPosition();
            menu.Items.Add(itemReset);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem itemExit = new ToolStripMenuItem("❌ Выход");
            itemExit.ForeColor = System.Drawing.Color.FromArgb(255, 120, 120);
            itemExit.Click += (s, e) => Shutdown();
            menu.Items.Add(itemExit);

            _trayIcon.ContextMenuStrip = menu;

            _trayIcon.DoubleClick += (s, e) => _mainWindow.ToggleVisibility();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            if (_appMutex != null)
            {
                _appMutex.ReleaseMutex();
                _appMutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}
