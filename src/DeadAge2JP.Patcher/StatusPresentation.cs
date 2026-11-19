using System.Globalization;
using DeadAge2JP.Core.Patching;

namespace DeadAge2JP.Patcher;

/// <summary>画面に出す状態表示。</summary>
/// <param name="Text">状態の説明。</param>
/// <param name="BrushKey">状態の色（App.xaml のリソースキー）。</param>
/// <param name="ButtonText">主ボタンの文言。</param>
/// <param name="IsRestoreMode">主ボタンが「元に戻す」として働くか。</param>
public sealed record StatusView(string Text, string BrushKey, string ButtonText, bool IsRestoreMode);

/// <summary>
/// パッチ状態から画面表示を決める純粋関数群。
/// </summary>
public static class StatusPresentation
{
    public const string ApplyText = "日本語化する";
    public const string RestoreText = "元に戻す";

    /// <summary>状態に応じた表示（文言・色・ボタン）を返す。</summary>
    public static StatusView For(PatchStatus status, PatchState? state) => status switch
    {
        PatchStatus.Patched => new StatusView(
            $"日本語化済み（{state?.AppliedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture) ?? "日時不明"} に適用）",
            "StatusGood", RestoreText, IsRestoreMode: true),
        PatchStatus.PartiallyPatched => new StatusView(
            "一部のファイルだけ日本語化されています（ゲームが更新された可能性があります）。「元に戻す」で適用前に戻せます。",
            "StatusWarn", RestoreText, IsRestoreMode: true),
        PatchStatus.GameFilesChanged => new StatusView(
            "ゲームの更新で日本語化が外れています。もう一度「日本語化する」を実行してください。",
            "StatusWarn", ApplyText, IsRestoreMode: false),
        _ => new StatusView("未適用（英語のまま）", "StatusNeutral", ApplyText, IsRestoreMode: false),
    };

    /// <summary>適用記録から日本語化率の説明を作る（記録が無ければ null）。</summary>
    public static string? Coverage(PatchState? state)
    {
        if (state is null || state.Tables.Count == 0)
        {
            return null;
        }
        // 分母は「訳す必要のある行数」。英語原文が空のキー（訳すものが無い）を含めると、全訳でも 100% にならないため除く
        return string.Join(" / ", state.Tables.Select(t =>
            $"{TableName(t.TableId)} {Percent(t.Translated, t.TranslatableRows)}（{t.Translated:N0}/{t.TranslatableRows:N0}）"));
    }

    /// <summary>表 ID の表示名。</summary>
    public static string TableName(string tableId) => tableId switch
    {
        "ui" => "UI",
        "dialogue" => "会話",
        _ => tableId,
    };

    private static string Percent(int part, int total) =>
        total == 0 ? "0%" : (100.0 * part / total).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
