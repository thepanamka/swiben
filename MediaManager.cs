using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace VinylNowPlaying
{
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
                // Select active session:
                // 1. First check GetCurrentSession()
                // 2. Also inspect GetSessions() to see if another player (e.g. Yandex Music) is currently Playing!
                object session = null;
                try
                {
                    MethodInfo getCurSession = _mgrType.GetMethod("GetCurrentSession", BindingFlags.Public | BindingFlags.Instance);
                    session = getCurSession.Invoke(_sessionManager, null);
                }
                catch { }

                // Check all sessions to find any actively playing session
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
                            if (status == 4) // Playing!
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
                            // If current is null, take the first available
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

                // 1. Playback status
                int statusInt = GetSessionPlaybackStatus(session);
                bool isPlaying = (statusInt == 4);

                if (isPlaying != _lastIsPlaying)
                {
                    _lastIsPlaying = isPlaying;
                    if (PlaybackStateChanged != null)
                        PlaybackStateChanged(isPlaying);
                }

                // 2. Media Properties
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

                    // 3. Timeline Properties
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

                        // Extract Thumbnail
                        byte[] thumbBytes = null;
                        try
                        {
                            object thumbRef = pt.GetProperty("Thumbnail").GetValue(props, null);
                            if (thumbRef != null)
                            {
                                thumbBytes = await ExtractThumbnailBytesAsync(thumbRef).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("Thumb error: " + ex.Message);
                        }

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
}
