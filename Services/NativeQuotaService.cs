using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IslandWidget.Models;

namespace IslandWidget.Services;

/// <summary>
/// Pure native C# quota fetcher — zero Node.js child process overhead!
/// </summary>
public sealed class NativeQuotaService : IDisposable
{
    private readonly HttpClient _loopbackClient;
    private HttpClient? _remoteClient;
    private string? _remoteProxySignature;
    private readonly object _clientLock = new();

    private string? _agyCachedAccessToken;
    private DateTimeOffset _agyCachedExpiry = DateTimeOffset.MinValue;

    public NativeQuotaService()
    {
        var loopbackHandler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };
        _loopbackClient = new HttpClient(loopbackHandler)
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
    }

    private HttpClient GetRemoteClient(string? proxyUrl)
    {
        var sig = proxyUrl ?? "<direct>";
        lock (_clientLock)
        {
            if (_remoteClient is not null && _remoteProxySignature == sig)
                return _remoteClient;

            _remoteClient?.Dispose();
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
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

            _remoteClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            _remoteProxySignature = sig;
            return _remoteClient;
        }
    }

    public async Task<QuotaAllResult> FetchAllAsync(AppSettings settings, bool demoMode, CancellationToken ct = default)
    {
        if (demoMode)
            return BuildDemoResult();

        var client = GetRemoteClient(settings.EffectiveProxy);

        var agyTask = FetchAntigravitySafeAsync(client, ct);
        var codexTask = FetchCodexSafeAsync(client, ct);
        var dshTask = FetchDshSafeAsync(client, ct);

        await Task.WhenAll(agyTask, codexTask, dshTask).ConfigureAwait(false);

        return new QuotaAllResult
        {
            Antigravity = await agyTask.ConfigureAwait(false),
            Codex = await codexTask.ConfigureAwait(false),
            Dsh = await dshTask.ConfigureAwait(false),
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private async Task<QuotaProviderSnapshot> FetchAntigravitySafeAsync(HttpClient remoteClient, CancellationToken ct)
    {
        try
        {
            // 1. Try local Antigravity language server first (includes both 5h and Weekly windows, zero external network!)
            try
            {
                var local = await TryFetchAntigravityLocalAsync(ct).ConfigureAwait(false);
                if (local is not null)
                    return local;
            }
            catch
            {
                // Fall back to remote Google quota endpoint
            }

            var cred = CredentialReader.ReadAntigravity();
            var token = _agyCachedAccessToken;
            var expiry = _agyCachedExpiry;
            if (string.IsNullOrWhiteSpace(token) || expiry <= DateTimeOffset.Now.AddMinutes(1))
            {
                token = cred.AccessToken;
                expiry = cred.ExpiresAt;
            }

            if (string.IsNullOrWhiteSpace(token) || expiry <= DateTimeOffset.Now.AddMinutes(1))
            {
                (token, expiry) = await RefreshAntigravityTokenAsync(remoteClient, cred.RefreshToken, ct).ConfigureAwait(false);
                _agyCachedAccessToken = token;
                _agyCachedExpiry = expiry;
            }

            try
            {
                return await FetchAntigravityRemoteAsync(remoteClient, token, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                (token, expiry) = await RefreshAntigravityTokenAsync(remoteClient, cred.RefreshToken, ct).ConfigureAwait(false);
                _agyCachedAccessToken = token;
                _agyCachedExpiry = expiry;
                return await FetchAntigravityRemoteAsync(remoteClient, token, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return new QuotaProviderSnapshot
            {
                Id = "antigravity", Name = "Antigravity", ShortTag = "A", Subtitle = "Gemini", AccentHex = "#A78BFA",
                Ok = false, Error = ShortenError(ex)
            };
        }
    }

    private async Task<QuotaProviderSnapshot?> TryFetchAntigravityLocalAsync(CancellationToken ct)
    {
        var daemonDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "daemon");
        if (!Directory.Exists(daemonDir)) return null;

        var hubFiles = Directory.GetFiles(daemonDir, "hub_*.json")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        foreach (var file in hubFiles)
        {
            int port = 0;
            string? csrf = null;
            try
            {
                using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("httpPort", out var pEl))
                {
                    port = pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32() : int.TryParse(pEl.GetString(), out var parsed) ? parsed : 0;
                }
                if (doc.RootElement.TryGetProperty("csrfToken", out var cEl))
                    csrf = cEl.GetString();
            }
            catch
            {
                continue;
            }

            if (port <= 0 || string.IsNullOrWhiteSpace(csrf)) continue;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary")
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.TryAddWithoutValidation("x-codeium-csrf-token", csrf);

                using var resp = await _loopbackClient.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var qDoc = JsonDocument.Parse(text);
                if (!qDoc.RootElement.TryGetProperty("response", out var respEl) ||
                    !respEl.TryGetProperty("groups", out var groupsEl) ||
                    groupsEl.ValueKind != JsonValueKind.Array)
                    continue;

                JsonElement? geminiGroup = null;
                foreach (var g in groupsEl.EnumerateArray())
                {
                    var dn = g.TryGetProperty("displayName", out var dEl) ? dEl.GetString() ?? "" : "";
                    if (dn.Contains("Gemini Models", StringComparison.OrdinalIgnoreCase))
                    {
                        geminiGroup = g;
                        break;
                    }
                    if (geminiGroup is null && dn.Contains("Gemini", StringComparison.OrdinalIgnoreCase) && !dn.Contains("Argon", StringComparison.OrdinalIgnoreCase))
                        geminiGroup = g;
                }

                if (geminiGroup is null) continue;
                var grp = geminiGroup.Value;
                QuotaWindowInfo? primary = null;
                QuotaWindowInfo? secondary = null;

                if (grp.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in buckets.EnumerateArray())
                    {
                        var win = b.TryGetProperty("window", out var wEl) ? wEl.GetString() ?? "" : "";
                        if (!b.TryGetProperty("remainingFraction", out var remEl) || !remEl.TryGetDouble(out var rem))
                            continue;
                        var resetStr = b.TryGetProperty("resetTime", out var rEl) ? FormatResetTime(rEl.GetString()) : "";
                        if (win.Equals("5h", StringComparison.OrdinalIgnoreCase))
                            primary = new QuotaWindowInfo { Label = "5 小时", RemainingFraction = Math.Clamp(rem, 0, 1), ResetText = resetStr };
                        else if (win.Equals("weekly", StringComparison.OrdinalIgnoreCase))
                            secondary = new QuotaWindowInfo { Label = "本周", RemainingFraction = Math.Clamp(rem, 0, 1), ResetText = resetStr };
                    }
                }

                if (primary is null && secondary is null) continue;

                var dispName = grp.TryGetProperty("displayName", out var dispEl) ? dispEl.GetString() ?? "Gemini" : "Gemini";
                dispName = Regex.Replace(dispName, @"\s*Models\s*$", "", RegexOptions.IgnoreCase);

                return new QuotaProviderSnapshot
                {
                    Id = "antigravity", Name = "Antigravity", ShortTag = "A", Subtitle = dispName, AccentHex = "#A78BFA",
                    Ok = true, Primary = primary, Secondary = secondary
                };
            }
            catch
            {
                // Try next hub file
            }
        }
        return null;
    }

    private static async Task<(string AccessToken, DateTimeOffset Expiry)> RefreshAntigravityTokenAsync(
        HttpClient client, string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = AntigravityOAuth.EffectiveClientId,
            ["client_secret"] = AntigravityOAuth.EffectiveClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var access = doc.RootElement.TryGetProperty("access_token", out var aEl) ? aEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(access))
            throw new InvalidOperationException("Antigravity 令牌刷新失败");
        var ttl = doc.RootElement.TryGetProperty("expires_in", out var eEl) && eEl.TryGetInt32(out var secs) && secs > 60 ? secs : 3599;
        return (access, DateTimeOffset.Now.AddSeconds(ttl));
    }

    private static async Task<QuotaProviderSnapshot> FetchAntigravityRemoteAsync(HttpClient client, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("User-Agent", "antigravity/2.33.0");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);

        double minRemaining = 1.0;
        bool found = false;
        string? earliestReset = null;

        if (doc.RootElement.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in buckets.EnumerateArray())
            {
                var modelId = b.TryGetProperty("modelId", out var mEl) ? mEl.GetString() ?? "" : "";
                if (!modelId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) continue;
                if (b.TryGetProperty("remainingFraction", out var rEl) && rEl.TryGetDouble(out var rem))
                {
                    found = true;
                    if (rem < minRemaining) minRemaining = rem;
                    if (b.TryGetProperty("resetTime", out var rtEl) && rtEl.GetString() is { Length: > 0 } rt)
                    {
                        if (earliestReset is null || string.CompareOrdinal(rt, earliestReset) < 0)
                            earliestReset = rt;
                    }
                }
            }
        }

        if (!found)
            throw new InvalidOperationException("远程未返回 Gemini 额度");

        return new QuotaProviderSnapshot
        {
            Id = "antigravity", Name = "Antigravity", ShortTag = "A", Subtitle = "Gemini · 仅5h", AccentHex = "#A78BFA",
            Ok = true,
            Note = "客户端未运行，仅显示 5 小时窗口",
            Primary = new QuotaWindowInfo
            {
                Label = "5 小时",
                RemainingFraction = Math.Clamp(minRemaining, 0, 1),
                ResetText = FormatResetTime(earliestReset)
            }
        };
    }

    private static async Task<QuotaProviderSnapshot> FetchCodexSafeAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            var cred = CredentialReader.ReadCodex();
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.AccessToken);
            req.Headers.TryAddWithoutValidation("chatgpt-account-id", cred.AccountId);
            req.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
            req.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs/0.153.4");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var plan = root.TryGetProperty("plan_type", out var pEl) ? pEl.GetString() ?? "ChatGPT" : "ChatGPT";

            QuotaWindowInfo? primary = null;
            QuotaWindowInfo? secondary = null;
            if (root.TryGetProperty("rate_limit", out var rl))
            {
                if (rl.TryGetProperty("primary_window", out var pw) && pw.ValueKind == JsonValueKind.Object)
                    primary = ParseCodexWindow(pw, "5 小时");
                if (rl.TryGetProperty("secondary_window", out var sw) && sw.ValueKind == JsonValueKind.Object)
                    secondary = ParseCodexWindow(sw, "7 天");
            }

            if (primary is null && secondary is null)
                throw new InvalidOperationException("未返回速率窗口");

            return new QuotaProviderSnapshot
            {
                Id = "codex", Name = "Codex", ShortTag = "C", Subtitle = plan.ToUpperInvariant(), AccentHex = "#34D399",
                Ok = true, Primary = primary, Secondary = secondary
            };
        }
        catch (Exception ex)
        {
            return new QuotaProviderSnapshot
            {
                Id = "codex", Name = "Codex", ShortTag = "C", Subtitle = "ChatGPT", AccentHex = "#34D399",
                Ok = false, Error = ShortenError(ex)
            };
        }
    }

    private static QuotaWindowInfo ParseCodexWindow(JsonElement win, string defaultLabel)
    {
        double usedPct = 0;
        if (win.TryGetProperty("used_percent", out var uEl) && uEl.TryGetDouble(out var u))
            usedPct = u;
        var rem = Math.Clamp(1.0 - usedPct / 100.0, 0, 1);

        string resetText = "";
        if (win.TryGetProperty("reset_at", out var rEl))
        {
            if (rEl.ValueKind == JsonValueKind.Number && rEl.TryGetInt64(out var unix))
                resetText = FormatDelta(DateTimeOffset.FromUnixTimeSeconds(unix) - DateTimeOffset.Now);
            else if (rEl.ValueKind == JsonValueKind.String)
                resetText = FormatResetTime(rEl.GetString());
        }
        return new QuotaWindowInfo { Label = defaultLabel, RemainingFraction = rem, ResetText = resetText };
    }

    private static async Task<QuotaProviderSnapshot> FetchDshSafeAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            var token = CredentialReader.ReadDshToken();
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://platform.deepseek.com/api/v0/users/get_user_summary");
            req.Headers.TryAddWithoutValidation("x-dsh-auth-token", token);
            req.Headers.TryAddWithoutValidation("User-Agent", "IslandWidget/1.0");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var cEl) && cEl.GetInt32() != 0)
            {
                var msg = root.TryGetProperty("msg", out var mEl) ? mEl.GetString() : "DSH 接口返回错误";
                throw new InvalidOperationException(msg);
            }

            var biz = root.GetProperty("data").GetProperty("biz_data");
            double normal = SumWalletArray(biz, "normal_wallets", "balance");
            double bonus = SumWalletArray(biz, "bonus_wallets", "balance");
            double spent = SumWalletArray(biz, "total_costs", "amount");

            return new QuotaProviderSnapshot
            {
                Id = "dsh", Name = "DSH", ShortTag = "¥", Subtitle = "DeepSeek", AccentHex = "#60A5FA",
                Ok = true, IsWallet = true,
                Balance = normal + bonus,
                Bonus = bonus,
                Spent = spent,
                Currency = "CNY"
            };
        }
        catch (Exception ex)
        {
            return new QuotaProviderSnapshot
            {
                Id = "dsh", Name = "DSH", ShortTag = "¥", Subtitle = "DeepSeek", AccentHex = "#60A5FA",
                Ok = false, IsWallet = true, Error = ShortenError(ex)
            };
        }
    }

    private static double SumWalletArray(JsonElement biz, string arrayProp, string valProp)
    {
        if (!biz.TryGetProperty(arrayProp, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return 0;
        double sum = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty(valProp, out var vEl))
            {
                if (vEl.ValueKind == JsonValueKind.Number && vEl.TryGetDouble(out var d))
                    sum += d;
                else if (vEl.ValueKind == JsonValueKind.String && double.TryParse(vEl.GetString(), out var ds))
                    sum += ds;
            }
        }
        return sum;
    }

    private static string FormatResetTime(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        if (!DateTimeOffset.TryParse(iso, out var target)) return "";
        return FormatDelta(target - DateTimeOffset.Now);
    }

    private static string FormatDelta(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero) return "即将重置";
        if (delta.TotalDays >= 1)
            return $"{(int)delta.TotalDays}天{delta.Hours}时后重置";
        if (delta.TotalHours >= 1)
            return $"{(int)delta.TotalHours}时{delta.Minutes}分后重置";
        return $"{Math.Max(1, delta.Minutes)}分后重置";
    }

    private static string ShortenError(Exception ex)
    {
        var msg = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return msg.Length > 48 ? msg[..48] + "…" : msg;
    }

    private static QuotaAllResult BuildDemoResult() => new()
    {
        Antigravity = new QuotaProviderSnapshot
        {
            Id = "antigravity", Name = "Antigravity", ShortTag = "A", Subtitle = "Gemini (演示)", AccentHex = "#A78BFA",
            Ok = true,
            Primary = new QuotaWindowInfo { Label = "5 小时", RemainingFraction = 0.82, ResetText = "3时18分后重置" },
            Secondary = new QuotaWindowInfo { Label = "本周", RemainingFraction = 0.68, ResetText = "4天11时后重置" }
        },
        Codex = new QuotaProviderSnapshot
        {
            Id = "codex", Name = "Codex", ShortTag = "C", Subtitle = "PRO (演示)", AccentHex = "#34D399",
            Ok = true,
            Primary = new QuotaWindowInfo { Label = "5 小时", RemainingFraction = 0.91, ResetText = "4时05分后重置" },
            Secondary = new QuotaWindowInfo { Label = "7 天", RemainingFraction = 0.74, ResetText = "5天02时后重置" }
        },
        Dsh = new QuotaProviderSnapshot
        {
            Id = "dsh", Name = "DSH", ShortTag = "¥", Subtitle = "DeepSeek (演示)", AccentHex = "#60A5FA",
            Ok = true, IsWallet = true, Balance = 68.50, Bonus = 10.00, Spent = 31.50, Currency = "CNY"
        }
    };

    public void Dispose()
    {
        _loopbackClient.Dispose();
        lock (_clientLock)
        {
            _remoteClient?.Dispose();
            _remoteClient = null;
        }
    }
}
