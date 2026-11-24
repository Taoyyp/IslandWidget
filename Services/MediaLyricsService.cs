using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace IslandWidget.Services;

public sealed record MediaTrackSnapshot(
    bool HasSession,
    string AppName,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    double PositionSec,
    double DurationSec,
    string CurrentLyric,
    string CurrentTransLyric,
    BitmapSource? Cover)
{
    public static readonly MediaTrackSnapshot Empty = new(false, "", "", "", "", false, 0, 0, "", "", null);
}

public sealed class MediaLyricsService : IDisposable
{
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private readonly HttpClient _http;
    private readonly NeteaseLogReader _netease = new();
    private readonly Dictionary<string, ParsedLyrics?> _lyricsCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lyricsPending = new(StringComparer.Ordinal);
    private readonly object _lyricLock = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _activeSession;
    private string _lastTrackKey = "";
    private BitmapSource? _cachedCover;
    private bool _disposed;

    public MediaTrackSnapshot Current { get; private set; } = MediaTrackSnapshot.Empty;

    public MediaLyricsService()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task<MediaTrackSnapshot> PollAsync(CancellationToken ct = default)
    {
        if (_disposed) return Current;
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch
        {
            _manager = null;
        }

        // 1. Check NetEase local log (gives exact millisecond position & songId without needing SMTC plugin)
        var ne = _netease.PollSnapshot();
        var sessions = _manager?.GetSessions();
        var bestSmtc = ChooseBestSession(sessions);

        bool smtcPlaying = false;
        string smtcAppId = "";
        if (bestSmtc is not null)
        {
            try
            {
                smtcAppId = bestSmtc.SourceAppUserModelId ?? "";
                var pb = bestSmtc.GetPlaybackInfo();
                smtcPlaying = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch { }
        }

        if (ne is not null && (bestSmtc is null || smtcAppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) || (ne.Value.Playing && !smtcPlaying)))
        {
            _activeSession = bestSmtc is not null && smtcAppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ? bestSmtc : null;
            var trackKey = $"ne:{ne.Value.SongId}";
            if (trackKey != _lastTrackKey)
            {
                _lastTrackKey = trackKey;
                _cachedCover = null;
                if (!string.IsNullOrWhiteSpace(ne.Value.CoverUrl))
                    _ = LoadUrlCoverAsync(trackKey, ne.Value.CoverUrl);
            }

            var (lyric, trans) = LookupLyricLine(ne.Value.SongId, ne.Value.Title, ne.Value.Artist, ne.Value.Duration, ne.Value.Position);
            Current = new MediaTrackSnapshot(
                true, "网易云音乐", ne.Value.Title, ne.Value.Artist, ne.Value.Album,
                ne.Value.Playing, ne.Value.Position, ne.Value.Duration,
                lyric, trans, _cachedCover);
            return Current;
        }

        _activeSession = bestSmtc;
        if (bestSmtc is null)
        {
            _lastTrackKey = "";
            _cachedCover = null;
            Current = MediaTrackSnapshot.Empty;
            return Current;
        }

        try
        {
            var props = await bestSmtc.TryGetMediaPropertiesAsync().AsTask(ct).ConfigureAwait(false);
            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            string album = props?.AlbumTitle ?? "";
            if (string.IsNullOrWhiteSpace(title))
            {
                Current = MediaTrackSnapshot.Empty;
                return Current;
            }

            var pb = bestSmtc.GetPlaybackInfo();
            bool playing = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            double pos = 0, dur = 0;
            try
            {
                var tl = bestSmtc.GetTimelineProperties();
                if (tl is not null)
                {
                    var start = tl.StartTime.TotalSeconds;
                    dur = Math.Max(0, tl.EndTime.TotalSeconds - start);
                    pos = Math.Max(0, tl.Position.TotalSeconds - start);
                    if (playing && tl.LastUpdatedTime.Year > 1601)
                    {
                        var age = (DateTimeOffset.UtcNow - tl.LastUpdatedTime).TotalSeconds;
                        if (age is > 0 and < 21600) pos += age;
                    }
                    if (dur > 0) pos = Math.Min(pos, dur);
                }
            }
            catch { }

            string key = $"smtc:{smtcAppId}:{title}:{artist}";
            if (key != _lastTrackKey)
            {
                _lastTrackKey = key;
                _cachedCover = await ReadSmtcCoverAsync(props?.Thumbnail).ConfigureAwait(false);
            }
            else if (_cachedCover is null && props?.Thumbnail is not null)
            {
                _cachedCover = await ReadSmtcCoverAsync(props.Thumbnail).ConfigureAwait(false);
            }

            var (lyric, trans) = LookupLyricLine("", title, artist, dur, pos);
            Current = new MediaTrackSnapshot(
                true, FriendlyAppName(smtcAppId), title, artist, album,
                playing, pos, dur, lyric, trans, _cachedCover);
            return Current;
        }
        catch
        {
            return Current;
        }
    }

    public async Task PlayPauseAsync()
    {
        try
        {
            if (_activeSession is not null && await _activeSession.TryTogglePlayPauseAsync().AsTask().ConfigureAwait(false))
                return;
        }
        catch { }
        SendMediaKey(VK_MEDIA_PLAY_PAUSE);
    }

    public async Task NextAsync()
    {
        try
        {
            if (_activeSession is not null && await _activeSession.TrySkipNextAsync().AsTask().ConfigureAwait(false))
                return;
        }
        catch { }
        SendMediaKey(VK_MEDIA_NEXT_TRACK);
    }

    public async Task PreviousAsync()
    {
        try
        {
            if (_activeSession is not null && await _activeSession.TrySkipPreviousAsync().AsTask().ConfigureAwait(false))
                return;
        }
        catch { }
        SendMediaKey(VK_MEDIA_PREV_TRACK);
    }

    private static void SendMediaKey(byte vk)
    {
        try
        {
            keybd_event(vk, 0, 0x0001, UIntPtr.Zero);
            keybd_event(vk, 0, 0x0001 | 0x0002, UIntPtr.Zero);
        }
        catch { }
    }

    private static GlobalSystemMediaTransportControlsSession? ChooseBestSession(IReadOnlyList<GlobalSystemMediaTransportControlsSession>? sessions)
    {
        if (sessions is null || sessions.Count == 0) return null;
        string[] prio = ["cloudmusic", "qqmusic", "spotify", "applemusic", "bilibili", "msedge", "chrome"];
        return sessions
            .Select(s =>
            {
                bool playing = false;
                try { playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; } catch { }
                var id = (s.SourceAppUserModelId ?? "").ToLowerInvariant();
                int rank = Array.FindIndex(prio, p => id.Contains(p, StringComparison.Ordinal));
                if (rank < 0) rank = prio.Length;
                return (PlayingOrder: playing ? 0 : 1, Rank: rank, Session: s);
            })
            .OrderBy(x => x.PlayingOrder)
            .ThenBy(x => x.Rank)
            .First().Session;
    }

    private static string FriendlyAppName(string appId)
    {
        var low = appId.ToLowerInvariant();
        if (low.Contains("cloudmusic")) return "网易云音乐";
        if (low.Contains("qqmusic")) return "QQ音乐";
        if (low.Contains("spotify")) return "Spotify";
        if (low.Contains("bilibili")) return "哔哩哔哩";
        if (low.Contains("msedge")) return "Edge";
        if (low.Contains("chrome")) return "Chrome";
        if (low.Contains("firefox")) return "Firefox";
        var name = appId.Split(['!', '\\', '/']).LastOrDefault() ?? "媒体";
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.IsNullOrWhiteSpace(name) ? "媒体" : name;
    }

    private async Task LoadUrlCoverAsync(string expectedKey, string url)
    {
        try
        {
            var httpsUrl = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "https://" + url[7..]
                : url;
            httpsUrl += (httpsUrl.Contains('?') ? "&" : "?") + "param=160y160";
            var bytes = await _http.GetByteArrayAsync(httpsUrl).ConfigureAwait(false);
            var bmp = DecodeFrozenBitmap(bytes, 140);
            if (bmp is not null && _lastTrackKey == expectedKey)
                _cachedCover = bmp;
        }
        catch { }
    }

    private static async Task<BitmapSource?> ReadSmtcCoverAsync(IRandomAccessStreamReference? thumb)
    {
        if (thumb is null) return null;
        try
        {
            using var stream = await thumb.OpenReadAsync().AsTask().ConfigureAwait(false);
            if (stream.Size is 0 or > 8 * 1024 * 1024) return null;
            var buf = new Windows.Storage.Streams.Buffer((uint)stream.Size);
            var read = await stream.ReadAsync(buf, (uint)stream.Size, InputStreamOptions.ReadAhead).AsTask().ConfigureAwait(false);
            return DecodeFrozenBitmap(read.ToArray(), 140);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? DecodeFrozenBitmap(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private (string Lyric, string Trans) LookupLyricLine(string songId, string title, string artist, double duration, double pos)
    {
        if (string.IsNullOrWhiteSpace(title)) return ("", "");
        string cacheKey = !string.IsNullOrWhiteSpace(songId) ? $"id:{songId}" : $"q:{title}|{artist}";

        ParsedLyrics? parsed;
        bool shouldFetch = false;
        lock (_lyricLock)
        {
            if (!_lyricsCache.TryGetValue(cacheKey, out parsed))
            {
                if (_lyricsPending.Add(cacheKey))
                    shouldFetch = true;
            }
        }

        if (shouldFetch)
        {
            _ = FetchLyricsInBackgroundAsync(cacheKey, songId, title, artist, duration);
            return ("", "");
        }

        return parsed is null ? ("", "") : parsed.At(pos);
    }

    private async Task FetchLyricsInBackgroundAsync(string cacheKey, string songId, string title, string artist, double duration)
    {
        ParsedLyrics? result = null;
        try
        {
            string id = songId;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = await SearchNeteaseSongIdAsync(title, artist, duration).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                var json = await _http.GetStringAsync($"https://music.163.com/api/song/lyric?id={Uri.EscapeDataString(id)}&lv=1&tv=-1").ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool noLyric = (root.TryGetProperty("nolyric", out var nl) && nl.ValueKind == JsonValueKind.True) ||
                               (root.TryGetProperty("uncollected", out var uc) && uc.ValueKind == JsonValueKind.True);
                if (!noLyric)
                {
                    var lrcText = root.TryGetProperty("lrc", out var lEl) && lEl.TryGetProperty("lyric", out var lyEl) ? lyEl.GetString() ?? "" : "";
                    var tText = root.TryGetProperty("tlyric", out var tEl) && tEl.TryGetProperty("lyric", out var tlEl) ? tlEl.GetString() ?? "" : "";
                    var parsed = ParsedLyrics.Parse(lrcText, tText);
                    if (parsed.HasLines) result = parsed;
                }
            }
        }
        catch { }

        lock (_lyricLock)
        {
            _lyricsPending.Remove(cacheKey);
            if (_lyricsCache.Count > 80)
                _lyricsCache.Clear();
            _lyricsCache[cacheKey] = result;
        }
    }

    private async Task<string> SearchNeteaseSongIdAsync(string title, string artist, double duration)
    {
        if (duration > 0 && (duration < 25 || duration > 960))
            return ""; // Likely a long video rather than a song

        var cleanTitle = Regex.Replace(title, @"\s*[\(（\[【].*?[\)）\]】]\s*", " ").Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = title;
        var q = $"{cleanTitle} {artist}".Trim();
        var url = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(q)}&type=1&limit=6";
        var json = await _http.GetStringAsync(url).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var rEl) ||
            !rEl.TryGetProperty("songs", out var sEl) ||
            sEl.ValueKind != JsonValueKind.Array)
            return "";

        string bestId = "";
        double bestDiff = double.MaxValue;
        foreach (var s in sEl.EnumerateArray())
        {
            var id = s.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
            double sDur = s.TryGetProperty("duration", out var dEl) && dEl.TryGetDouble(out var ms) ? ms / 1000.0 : 0;
            double diff = duration > 0 && sDur > 0 ? Math.Abs(sDur - duration) : 0;
            if (duration > 0 && diff > 10) continue;
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestId = id;
            }
        }
        return bestId;
    }

    public void Dispose()
    {
        _disposed = true;
        _http.Dispose();
    }

    private sealed class ParsedLyrics
    {
        private static readonly Regex TsRegex = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
        private static readonly Regex CreditRegex = new(@"^\s*[^\s:：]{1,10}\s*[:：]", RegexOptions.Compiled);

        private readonly double[] _times;
        private readonly string[] _lines;
        private readonly string[] _trans;

        public bool HasLines => _lines.Length > 0;

        private ParsedLyrics(double[] times, string[] lines, string[] trans)
        {
            _times = times;
            _lines = lines;
            _trans = trans;
        }

        public static ParsedLyrics Parse(string lrc, string tlrc)
        {
            var main = ParseSingle(lrc);
            var tr = ParseSingle(tlrc);
            var trMap = new Dictionary<int, string>();
            foreach (var (t, s) in tr)
            {
                if (!string.IsNullOrWhiteSpace(s))
                    trMap[(int)Math.Round(t * 10)] = s;
            }

            var times = new double[main.Count];
            var lines = new string[main.Count];
            var trans = new string[main.Count];
            for (int i = 0; i < main.Count; i++)
            {
                times[i] = main[i].Time;
                lines[i] = main[i].Text;
                trMap.TryGetValue((int)Math.Round(main[i].Time * 10), out var tline);
                trans[i] = tline ?? "";
            }
            return new ParsedLyrics(times, lines, trans);
        }

        private static List<(double Time, string Text)> ParseSingle(string rawText)
        {
            var list = new List<(double Time, string Text)>();
            if (string.IsNullOrWhiteSpace(rawText)) return list;

            foreach (var raw in rawText.Split('\n'))
            {
                var matches = TsRegex.Matches(raw);
                if (matches.Count == 0) continue;
                var last = matches[^1];
                var body = raw[(last.Index + last.Length)..].Trim();
                foreach (Match m in matches)
                {
                    var frac = m.Groups[3].Success ? m.Groups[3].Value : "0";
                    double t = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) +
                               int.Parse(frac) / Math.Pow(10, frac.Length);
                    list.Add((t, body));
                }
            }
            list.Sort((a, b) => a.Time.CompareTo(b.Time));
            int skip = 0;
            while (skip < list.Count && (string.IsNullOrEmpty(list[skip].Text) || (list[skip].Time < 35 && CreditRegex.IsMatch(list[skip].Text))))
                skip++;
            return skip > 0 ? list.GetRange(skip, list.Count - skip) : list;
        }

        public (string Lyric, string Trans) At(double posSec)
        {
            if (_times.Length == 0) return ("", "");
            int idx = Array.BinarySearch(_times, posSec + 0.15);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0) return ("", "");
            return (_lines[idx], _trans[idx]);
        }
    }

    /// <summary>
    /// Decodes %LOCALAPPDATA%\NetEase\CloudMusic\cloudmusic.elog without plugins (ported from Wisland MIT).
    /// </summary>
    private sealed class NeteaseLogReader
    {
        public readonly record struct NeteaseState(string SongId, string Title, string Artist, string Album, string CoverUrl, double Duration, double Position, bool Playing);

        private static readonly byte[] DecodeTable = BuildDecodeTable();
        private static readonly byte EncodedNewline = BuildEncodedNewline();
        private static readonly Regex HeaderRe = new(@"^\[(\d+):(\d+):(\d{4}/\d{6}):(\d+):([A-Z]+):", RegexOptions.Compiled);
        private static readonly Regex SeekRe = new(@"""setPlayingPosition""\s*,\s*(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
        private static readonly Regex StateRe = new(@"""native[^""]*state""\s*,\s*(\d+)", RegexOptions.Compiled);

        private readonly string _elogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetEase", "CloudMusic", "cloudmusic.elog");
        private long _offset = -1;
        private byte[] _carry = [];
        private string _songId = "", _title = "", _artist = "", _album = "", _cover = "";
        private double _duration, _pos, _anchorSec;
        private bool _playing;
        private int _pid;

        private static byte[] BuildDecodeTable()
        {
            var t = new byte[256];
            for (int b = 0; b < 256; b++)
                t[b] = (byte)(((((b >> 4) ^ (((b & 15) + 8) % 16)) % 16) * 16 + (b >> 6) * 4 + ((~(b >> 4)) & 3)) & 0xFF);
            return t;
        }

        private static byte BuildEncodedNewline()
        {
            for (int b = 0; b < 256; b++)
                if (DecodeTable[b] == 0x0A) return (byte)b;
            return 0x0A;
        }

        public NeteaseState? PollSnapshot()
        {
            if (!File.Exists(_elogPath))
            {
                _offset = -1;
                Reset();
                return null;
            }

            try
            {
                var fi = new FileInfo(_elogPath);
                long size = fi.Length;
                if (_offset < 0 || size < _offset)
                {
                    Reset();
                    long start = Math.Max(0, size - 2 * 1024 * 1024);
                    _carry = [];
                    ReadChunk(start, size, start > 0);
                }
                else if (size > _offset)
                {
                    ReadChunk(_offset, size, false);
                }
                _offset = size;
            }
            catch { }

            if (string.IsNullOrWhiteSpace(_title)) return null;
            if (_pid > 0)
            {
                try { using var p = Process.GetProcessById(_pid); if (p.HasExited) { Reset(); return null; } }
                catch { Reset(); return null; }
            }

            double nowSec = Environment.TickCount64 / 1000.0;
            double pos = _playing ? _pos + Math.Max(0, nowSec - _anchorSec) : _pos;
            if (_duration > 0) pos = Math.Min(pos, _duration);
            return new NeteaseState(_songId, _title, _artist, _album, _cover, _duration, pos, _playing);
        }

        private void Reset()
        {
            _songId = _title = _artist = _album = _cover = "";
            _duration = _pos = _anchorSec = 0;
            _playing = false;
            _pid = 0;
        }

        private void ReadChunk(long start, long end, bool skipPartial)
        {
            int toRead = (int)Math.Min(4 * 1024 * 1024, end - start);
            if (toRead <= 0) return;
            byte[] raw = new byte[toRead];
            using (var fs = new FileStream(_elogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(start, SeekOrigin.Begin);
                int got = fs.Read(raw, 0, toRead);
                if (got <= 0) return;
                if (got < toRead) Array.Resize(ref raw, got);
            }

            int startIdx = 0;
            if (skipPartial)
            {
                int nl = Array.IndexOf(raw, EncodedNewline);
                if (nl < 0) return;
                startIdx = nl + 1;
            }

            var combined = new byte[_carry.Length + (raw.Length - startIdx)];
            System.Buffer.BlockCopy(_carry, 0, combined, 0, _carry.Length);
            System.Buffer.BlockCopy(raw, startIdx, combined, _carry.Length, raw.Length - startIdx);

            int cut = Array.LastIndexOf(combined, EncodedNewline);
            if (cut < 0)
            {
                _carry = combined.Length > 512 * 1024 ? combined[^524288..] : combined;
                return;
            }

            int rem = combined.Length - (cut + 1);
            _carry = rem > 0 ? combined[(cut + 1)..Math.Min(combined.Length, cut + 1 + 524288)] : [];

            var decoded = new byte[cut + 1];
            for (int i = 0; i <= cut; i++)
                decoded[i] = DecodeTable[combined[i]];

            var text = Encoding.UTF8.GetString(decoded);
            foreach (var line in text.Split('\n'))
                ProcessLine(line);
        }

        private void ProcessLine(string line)
        {
            if (!line.StartsWith('[') || (!line.Contains("【playing】", StringComparison.Ordinal) && !line.Contains("exit", StringComparison.Ordinal)))
                return;
            var hm = HeaderRe.Match(line);
            if (!hm.Success) return;
            int pid = int.TryParse(hm.Groups[1].Value, out var p) ? p : 0;
            double atSec = long.TryParse(hm.Groups[4].Value, out var tickMs) ? tickMs / 1000.0 : Environment.TickCount64 / 1000.0;

            if (line.Contains("exitApp", StringComparison.Ordinal)) { Reset(); return; }
            if (line.Contains("\"checkPlayPrivilege\"", StringComparison.Ordinal) || line.Contains("\"playOneTrackInPlayingList\"", StringComparison.Ordinal))
            {
                int a = line.IndexOf('{'), b = line.LastIndexOf('}');
                if (a >= 0 && b > a)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line[a..(b + 1)]);
                        var tr = doc.RootElement.TryGetProperty("track", out var tEl) && tEl.ValueKind == JsonValueKind.Object ? tEl : doc.RootElement;
                        if (tr.TryGetProperty("id", out var idEl))
                        {
                            var sid = idEl.ToString();
                            if (!string.IsNullOrWhiteSpace(sid) && sid != _songId)
                            {
                                _songId = sid;
                                _pid = pid;
                                _title = tr.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                                _duration = tr.TryGetProperty("duration", out var dEl) && dEl.TryGetDouble(out var ms) ? ms / 1000.0 : 0;
                                _album = tr.TryGetProperty("album", out var alEl) && alEl.ValueKind == JsonValueKind.Object && alEl.TryGetProperty("name", out var aln) ? aln.GetString() ?? "" : "";
                                _cover = tr.TryGetProperty("album", out var alEl2) && alEl2.ValueKind == JsonValueKind.Object && alEl2.TryGetProperty("picUrl", out var pu) ? pu.GetString() ?? "" : "";
                                if (tr.TryGetProperty("artists", out var arEl) && arEl.ValueKind == JsonValueKind.Array)
                                    _artist = string.Join(" / ", arEl.EnumerateArray().Select(x => x.TryGetProperty("name", out var an) ? an.GetString() : null).Where(s => !string.IsNullOrWhiteSpace(s)));
                                _playing = line.Contains("\"playOneTrackInPlayingList\"", StringComparison.Ordinal);
                                _pos = 0; _anchorSec = atSec;
                            }
                        }
                    }
                    catch { }
                }
                return;
            }

            var sk = SeekRe.Match(line);
            if (sk.Success && double.TryParse(sk.Groups[1].Value, out var seekPos))
            {
                _pos = Math.Max(0, seekPos);
                _anchorSec = atSec;
                return;
            }

            var st = StateRe.Match(line);
            if (st.Success)
            {
                double cur = _playing ? _pos + Math.Max(0, atSec - _anchorSec) : _pos;
                _playing = st.Groups[1].Value == "1";
                _pos = cur;
                _anchorSec = atSec;
            }
        }
    }
}
