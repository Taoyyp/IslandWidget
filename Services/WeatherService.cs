using System.Net;
using System.Net.Http;
using System.Text.Json;
using IslandWidget.Models;

namespace IslandWidget.Services;

public sealed record DailyWeatherItem(string DateLabel, string Glyph, string Desc, int MinC, int MaxC);

public sealed record WeatherSnapshot(
    bool Ok,
    string City,
    int TempC,
    int FeelsLikeC,
    string Desc,
    string Glyph,
    int Humidity,
    double WindSpeedMs,
    IReadOnlyList<DailyWeatherItem> Forecast,
    string Error = "")
{
    public static readonly WeatherSnapshot Empty = new(false, "北京", 0, 0, "加载中", "☀", 0, 0, []);
}

/// <summary>
/// Free, zero-API-key weather service powered by Open-Meteo (ported from Wisland MIT).
/// </summary>
public sealed class WeatherService : IDisposable
{
    private HttpClient? _client;
    private string? _proxySig;
    private readonly object _lock = new();

    public WeatherSnapshot Current { get; private set; } = WeatherSnapshot.Empty;

    private HttpClient GetClient(string? proxyUrl)
    {
        var sig = proxyUrl ?? "<direct>";
        lock (_lock)
        {
            if (_client is not null && _proxySig == sig) return _client;
            _client?.Dispose();
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(8),
                AutomaticDecompression = DecompressionMethods.All
            };
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(proxyUrl) { BypassProxyOnLocal = true };
            }
            else
            {
                handler.UseProxy = false;
            }
            _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            _proxySig = sig;
            return _client;
        }
    }

    public async Task<(bool Found, double Lat, double Lon, string ResolvedName)> GeocodeCityAsync(string cityQuery, AppSettings settings, CancellationToken ct = default)
    {
        try
        {
            var client = GetClient(settings.EffectiveProxy);
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(cityQuery.Trim())}&count=1&language=zh&format=json";
            var json = await client.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array && res.GetArrayLength() > 0)
            {
                var first = res[0];
                double lat = first.GetProperty("latitude").GetDouble();
                double lon = first.GetProperty("longitude").GetDouble();
                string name = first.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? cityQuery : cityQuery;
                return (true, Math.Round(lat, 4), Math.Round(lon, 4), name);
            }
        }
        catch { }
        return (false, 0, 0, cityQuery);
    }

    public async Task<WeatherSnapshot> RefreshAsync(AppSettings settings, bool demoMode = false, CancellationToken ct = default)
    {
        if (demoMode)
        {
            Current = new WeatherSnapshot(true, settings.WeatherCity, 14, 12, "晴朗", "☀", 36, 2.4,
            [
                new("今天", "☀", "晴", 4, 15),
                new("明天", "⛅", "多云", 5, 14),
                new("后天", "☀", "晴", 3, 13),
                new("周四", "☁", "阴", 2, 11),
                new("周五", "☀", "晴", 3, 14)
            ]);
            return Current;
        }

        try
        {
            double lat = settings.WeatherLat ?? 39.9042;
            double lon = settings.WeatherLon ?? 116.4074;

            if (!settings.WeatherLat.HasValue || !settings.WeatherLon.HasValue)
            {
                var geo = await GeocodeCityAsync(settings.WeatherCity, settings, ct).ConfigureAwait(false);
                if (geo.Found)
                {
                    lat = geo.Lat;
                    lon = geo.Lon;
                    settings.WeatherLat = lat;
                    settings.WeatherLon = lon;
                }
            }

            var client = GetClient(settings.EffectiveProxy);
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.####}&longitude={lon:0.####}&timezone=auto&wind_speed_unit=ms&forecast_days=5" +
                      "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m" +
                      "&daily=weather_code,temperature_2m_max,temperature_2m_min";

            var json = await client.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cur = root.GetProperty("current");

            int temp = (int)Math.Round(cur.GetProperty("temperature_2m").GetDouble());
            int feels = (int)Math.Round(cur.GetProperty("apparent_temperature").GetDouble());
            int hum = cur.TryGetProperty("relative_humidity_2m", out var hEl) ? hEl.GetInt32() : 0;
            double wind = cur.TryGetProperty("wind_speed_10m", out var wEl) ? wEl.GetDouble() : 0;
            int wmo = cur.TryGetProperty("weather_code", out var cEl) ? cEl.GetInt32() : 0;
            bool isDay = !cur.TryGetProperty("is_day", out var dEl) || dEl.GetInt32() != 0;

            var (desc, glyph) = MapWmo(wmo, isDay);

            var forecast = new List<DailyWeatherItem>();
            if (root.TryGetProperty("daily", out var daily))
            {
                var dates = daily.GetProperty("time");
                var codes = daily.GetProperty("weather_code");
                var maxs = daily.GetProperty("temperature_2m_max");
                var mins = daily.GetProperty("temperature_2m_min");
                int n = Math.Min(5, dates.GetArrayLength());
                for (int i = 0; i < n; i++)
                {
                    var dtStr = dates[i].GetString() ?? "";
                    string label = i == 0 ? "今天" : i == 1 ? "明天" : (DateTime.TryParse(dtStr, out var dt) ? dt.ToString("MM/dd") : dtStr);
                    var (ddesc, dglyph) = MapWmo(codes[i].GetInt32(), true);
                    forecast.Add(new DailyWeatherItem(label, dglyph, ddesc, (int)Math.Round(mins[i].GetDouble()), (int)Math.Round(maxs[i].GetDouble())));
                }
            }

            Current = new WeatherSnapshot(true, settings.WeatherCity, temp, feels, desc, glyph, hum, wind, forecast);
            return Current;
        }
        catch (Exception ex)
        {
            if (Current.Ok)
                return Current with { Error = ex.Message };
            Current = WeatherSnapshot.Empty with { City = settings.WeatherCity, Desc = "离线", Error = ex.Message };
            return Current;
        }
    }

    private static (string Desc, string Glyph) MapWmo(int code, bool day) => code switch
    {
        0 => ("晴", day ? "☀" : "☾"),
        1 => ("少云", day ? "🌤" : "☁"),
        2 => ("多云", "⛅"),
        3 => ("阴", "☁"),
        45 or 48 => ("雾", "🌫"),
        51 or 53 or 55 or 56 or 57 => ("毛毛雨", "🌦"),
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => ("雨", "🌧"),
        71 or 73 or 75 or 77 or 85 or 86 => ("雪", "❄"),
        95 or 96 or 99 => ("雷雨", "🌩"),
        _ => ("多云", "☁")
    };

    public void Dispose()
    {
        lock (_lock)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
