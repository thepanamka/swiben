using System;
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
                MethodInfo getSession = _mgrType.GetMethod("GetCurrentSession", BindingFlags.Public | BindingFlags.Instance);
                object session = getSession.Invoke(_sessionManager, null);
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

                // 1. Playback Info
                MethodInfo getPlayback = sessionType.GetMethod("GetPlaybackInfo");
                object playbackInfo = getPlayback.Invoke(session, null);
                bool isPlaying = false;

                if (playbackInfo != null)
                {
                    object statusObj = playbackInfo.GetType().GetProperty("PlaybackStatus").GetValue(playbackInfo, null);
                    int statusInt = Convert.ToInt32(statusObj);
                    // 4 = Playing
                    isPlaying = (statusInt == 4);
                }

                if (isPlaying != _lastIsPlaying)
                {
                    _lastIsPlaying = isPlaying;
                    if (PlaybackStateChanged != null)
                        PlaybackStateChanged(isPlaying);
                }

                // 2. Media Properties
                object propOp = sessionType.GetMethod("TryGetMediaPropertiesAsync").Invoke(session, null);
                Type propOpType = propOp.GetType();
                Type propType = propOpType.GetGenericArguments()[0];

                MethodInfo asTaskProp = _asTaskGeneric.MakeGenericMethod(propType);
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
                System.Diagnostics.Debug.WriteLine("MediaManager Update Error: " + ex.Message);
            }
        }

        private async Task<byte[]> ExtractThumbnailBytesAsync(object thumbRef)
        {
            if (thumbRef == null) return null;
            Type refType = thumbRef.GetType();
            MethodInfo openMethod = refType.GetMethod("OpenReadAsync");
            if (openMethod == null) return null;

            object asyncOp = openMethod.Invoke(thumbRef, null);
            Type opType = asyncOp.GetType();
            Type streamType = opType.GetGenericArguments()[0];

            MethodInfo asTask = _asTaskGeneric.MakeGenericMethod(streamType);
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
            Type opType = asyncOp.GetType();
            Type resType = opType.GetGenericArguments()[0];
            MethodInfo asTask = _asTaskGeneric.MakeGenericMethod(resType);
            Task t = (Task)asTask.Invoke(null, new object[] { asyncOp });
            await t.ConfigureAwait(false);
        }

        private string CleanAppId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Музыка";
            string lower = raw.ToLowerInvariant();
            if (lower.Contains("spotify")) return "Spotify";
            if (lower.Contains("yandex")) return "Яндекс Музыка";
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

            // If it's a path or package id, extract base name
            int lastSlash = Math.Max(raw.LastIndexOf('\\'), raw.LastIndexOf('/'));
            if (lastSlash >= 0 && lastSlash < raw.Length - 1)
                raw = raw.Substring(lastSlash + 1);
            if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(0, raw.Length - 4);

            return raw;
        }
    }
}
