using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IslandWidget.Models;

namespace IslandWidget.Services;

public static class AntigravityOAuth
{
    // Local Antigravity loopback service requires no OAuth client credentials.
    // For remote fallback refresh, provide QUOTA_AGY_CLIENT_ID and QUOTA_AGY_CLIENT_SECRET via environment variables.
    public static string EffectiveClientId => Environment.GetEnvironmentVariable("QUOTA_AGY_CLIENT_ID") ?? "";
    public static string EffectiveClientSecret => Environment.GetEnvironmentVariable("QUOTA_AGY_CLIENT_SECRET") ?? "";
}

public static class CredentialReader
{
    private const int CRED_TYPE_GENERIC = 1;

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    public static AntigravityCredential ReadAntigravity()
    {
        const string target = "gemini:antigravity";
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var pointer))
        {
            var code = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(code == 1168
                ? "未找到 Antigravity 登录凭据"
                : $"读取凭据失败 ({code})");
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                throw new InvalidOperationException("Antigravity 登录凭据为空");

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var json = Encoding.UTF8.GetString(bytes).TrimEnd('\0');

            using var document = JsonDocument.Parse(json);
            var token = document.RootElement.GetProperty("token");
            var access = token.TryGetProperty("access_token", out var accEl) ? accEl.GetString() ?? "" : "";
            var refresh = token.TryGetProperty("refresh_token", out var refEl) ? refEl.GetString() ?? "" : "";

            var expiry = DateTimeOffset.MinValue;
            if (token.TryGetProperty("expiry", out var expEl) && DateTimeOffset.TryParse(expEl.GetString(), out var parsed))
                expiry = parsed;

            if (string.IsNullOrWhiteSpace(refresh))
                throw new InvalidOperationException("缺少 refresh_token");

            return new AntigravityCredential
            {
                AccessToken = access,
                RefreshToken = refresh,
                ExpiresAt = expiry
            };
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static CodexCredential ReadCodex()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
        if (!File.Exists(path))
            throw new InvalidOperationException("未登录 Codex CLI");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var mode = root.TryGetProperty("auth_mode", out var modeEl) ? modeEl.GetString() : null;
        if (!string.Equals(mode, "chatgpt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Codex 非 ChatGPT 订阅模式");

        if (!root.TryGetProperty("tokens", out var tokens))
            throw new InvalidOperationException("Codex 缺少 tokens");

        var access = tokens.TryGetProperty("access_token", out var accEl) ? accEl.GetString() : null;
        var account = tokens.TryGetProperty("account_id", out var idEl) ? idEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(account))
            throw new InvalidOperationException("Codex 登录信息不完整");

        return new CodexCredential { AccessToken = access, AccountId = account };
    }

    public static string ReadDshToken()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", ".credentials.yaml");
        if (!File.Exists(path))
            throw new InvalidOperationException("未登录 DSH 账户");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = reader.ReadToEnd().Split('\n');

        var section = Array.FindIndex(lines, l => l.TrimStart().StartsWith("deepseek-account-platform/default:", StringComparison.Ordinal));
        if (section < 0)
            throw new InvalidOperationException("DSH 无平台账户 token");

        for (var i = section + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (!line.StartsWith(' ') && !line.StartsWith('\t')) break;
            if (trimmed.StartsWith("token:", StringComparison.Ordinal))
            {
                var token = trimmed["token:".Length..].Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(token))
                    return token;
            }
        }
        throw new InvalidOperationException("DSH token 为空");
    }
}
