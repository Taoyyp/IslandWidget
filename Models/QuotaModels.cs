using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IslandWidget.Models;

public sealed class AntigravityCredential
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class CodexCredential
{
    public string AccessToken { get; init; } = "";
    public string AccountId { get; init; } = "";
}

public sealed class QuotaWindowInfo
{
    public string Label { get; init; } = "";
    public double RemainingFraction { get; init; } // 0..1
    public string ResetText { get; init; } = "";
    public int RemainingPercent => (int)Math.Round(Math.Clamp(RemainingFraction, 0, 1) * 100);
    public int UsedPercent => 100 - RemainingPercent;
}

public sealed class QuotaProviderSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ShortTag { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string AccentHex { get; init; } = "#A78BFA";
    public bool Ok { get; init; }
    public string Error { get; init; } = "";
    public string Note { get; init; } = "";

    // Rate-window providers (Antigravity, Codex)
    public QuotaWindowInfo? Primary { get; init; }
    public QuotaWindowInfo? Secondary { get; init; }

    // Wallet provider (DSH / DeepSeek)
    public bool IsWallet { get; init; }
    public double Balance { get; init; }
    public double Bonus { get; init; }
    public double Spent { get; init; }
    public string Currency { get; init; } = "CNY";

    // Normalized 0..1 remaining fraction for collapsed ring
    public double RingRemainingFraction
    {
        get
        {
            if (!Ok) return 0;
            if (IsWallet)
            {
                // Scale relative to max(50, Balance + Spent * 0.1) or 100 CNY cap for visual ring
                return Math.Clamp(Balance / 100.0, 0.05, 1.0);
            }
            if (Primary is not null && Secondary is not null)
                return Math.Min(Primary.RemainingFraction, Secondary.RemainingFraction);
            return Primary?.RemainingFraction ?? Secondary?.RemainingFraction ?? 0;
        }
    }

    public string CompactSummary
    {
        get
        {
            if (!Ok) return "—";
            if (IsWallet)
            {
                var sym = Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ? "$" : "¥";
                return $"{sym}{Balance:0.##}";
            }
            if (Primary is not null && Secondary is not null)
                return $"{Primary.RemainingPercent}%/{Secondary.RemainingPercent}%";
            if (Primary is not null)
                return $"{Primary.RemainingPercent}%";
            return "—";
        }
    }
}

public sealed class QuotaAllResult
{
    public QuotaProviderSnapshot Antigravity { get; init; } = new() { Id = "antigravity", Name = "Antigravity", ShortTag = "A", Subtitle = "Gemini", AccentHex = "#A78BFA" };
    public QuotaProviderSnapshot Codex { get; init; } = new() { Id = "codex", Name = "Codex", ShortTag = "C", Subtitle = "ChatGPT", AccentHex = "#34D399" };
    public QuotaProviderSnapshot Dsh { get; init; } = new() { Id = "dsh", Name = "DSH", ShortTag = "¥", Subtitle = "DeepSeek", AccentHex = "#60A5FA", IsWallet = true };
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public bool AnyOk => Antigravity.Ok || Codex.Ok || Dsh.Ok;
}
