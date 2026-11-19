using System.Text.RegularExpressions;

namespace DeadAge2JP.Core.Translation;

/// <summary>
/// 訳文の機械検査で見つかった問題。
/// </summary>
/// <param name="Key">対象キー。</param>
/// <param name="Missing">原文にあるが訳文に無いトークン。</param>
/// <param name="Extra">訳文にあるが原文に無いトークン。</param>
public sealed record TokenMismatch(string Key, IReadOnlyList<string> Missing, IReadOnlyList<string> Extra);

/// <summary>
/// 原文と訳文で、ゲームが解釈する記号（差し込み・書式タグ）が過不足なく保たれているかを検査する。
/// <para>
/// 対象: [Player] や [Party_1_Name] 等の角括弧トークン、[b]～[/b] 等の書式タグ、
/// {0} 等の書式指定、&lt;color=...&gt; 等のリッチテキストタグ。順序の入れ替えは許容する（日本語の語順のため）。
/// </para>
/// <para>
/// 例外として、訳文で足した改行禁止タグ &lt;nobr&gt;…&lt;/nobr&gt; は、開きと閉じが正しくそろっていれば不一致にしない
/// （詳細は <see cref="WithoutAddedNoBreakPairs"/>）。
/// </para>
/// </summary>
public static partial class TokenValidator
{
    /// <summary>TextMeshPro の改行禁止タグ（開き）。囲んだ範囲の途中では行を折り返さない。</summary>
    private const string NoBreakOpen = "<nobr>";

    /// <summary>TextMeshPro の改行禁止タグ（閉じ）。</summary>
    private const string NoBreakClose = "</nobr>";

    /// <summary>角括弧トークン（入れ子なし・改行なし・80 文字以内）。</summary>
    [GeneratedRegex(@"\[[^\[\]\r\n]{1,80}\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketToken();

    /// <summary>string.Format の差し込み（{0}, {1:N0} など）。</summary>
    [GeneratedRegex(@"\{\d+(?:[,:][^{}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex FormatToken();

    /// <summary>TextMeshPro のリッチテキストタグ。</summary>
    [GeneratedRegex(@"<[^<>\r\n]{1,60}>", RegexOptions.CultureInvariant)]
    private static partial Regex RichTextToken();

    /// <summary>文字列に含まれる検査対象トークンを出現順に列挙する。</summary>
    public static IReadOnlyList<string> ExtractTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return BracketToken().Matches(text).Select(m => m.Value)
            .Concat(FormatToken().Matches(text).Select(m => m.Value))
            .Concat(RichTextToken().Matches(text).Select(m => m.Value))
            .ToList();
    }

    /// <summary>
    /// 検査対象トークンの位置（開始位置と長さ）を列挙する（種類ごとの出現順。重なりは呼び出し側で扱う）。
    /// 表記の正規化などで、記号の中身を変えずに文字列を加工するために使う（記号の定義を 1 か所にそろえる）。
    /// </summary>
    public static IReadOnlyList<(int Index, int Length)> ExtractTokenSpans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return BracketToken().Matches(text)
            .Concat(FormatToken().Matches(text))
            .Concat(RichTextToken().Matches(text))
            .Select(m => (m.Index, m.Length))
            .ToList();
    }

    /// <summary>
    /// 原文と訳文のトークンを多重集合として比較し、差があれば不一致情報を返す（一致なら null）。
    /// </summary>
    public static TokenMismatch? Compare(string key, string english, string japanese)
    {
        var expected = CountTokens(ExtractTokens(english));
        var actual = WithoutAddedNoBreakPairs(CountTokens(ExtractTokens(japanese)), expected, japanese);

        var missing = expected
            .SelectMany(kv => Enumerable.Repeat(kv.Key, Math.Max(0, kv.Value - actual.GetValueOrDefault(kv.Key))))
            .ToList();
        var extra = actual
            .SelectMany(kv => Enumerable.Repeat(kv.Key, Math.Max(0, kv.Value - expected.GetValueOrDefault(kv.Key))))
            .ToList();

        return missing.Count == 0 && extra.Count == 0 ? null : new TokenMismatch(key, missing, extra);
    }

    private static Dictionary<string, int> CountTokens(IEnumerable<string> tokens) =>
        tokens.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>
    /// 訳文で足した改行禁止タグ &lt;nobr&gt;…&lt;/nobr&gt; の組を、比較の対象から外した数え上げを返す（元の辞書は変えない）。
    /// <para>
    /// TextMeshPro は日本語をどの文字の間でも折り返すため、狭い欄では人名などの語が行をまたいで割れる
    /// （実機で「チャール／ズ将軍」「イン／ベントリ」を確認）。これを防ぐために、訳文で語を囲むことは許す。
    /// ゲームは訳文を TextMeshPro にそのまま渡し、勢力の説明などは &lt;color&gt; で囲んで表示しているので、
    /// リッチテキストとして解釈される。
    /// </para>
    /// <para>
    /// ただし、足した開きと閉じの数が同じで、かつ「開き → 閉じ」の順に入れ子なしで並んでいる場合に限る。
    /// 閉じ忘れがあると以降の文がすべて折り返さなくなり、TextMeshPro は入れ子を数えない（最初の閉じで解除される）ため、
    /// そろっていなければ外さずに余分として報告させる。
    /// </para>
    /// </summary>
    private static Dictionary<string, int> WithoutAddedNoBreakPairs(
        Dictionary<string, int> actual, IReadOnlyDictionary<string, int> expected, string japanese)
    {
        var addedOpen = actual.GetValueOrDefault(NoBreakOpen) - expected.GetValueOrDefault(NoBreakOpen);
        var addedClose = actual.GetValueOrDefault(NoBreakClose) - expected.GetValueOrDefault(NoBreakClose);
        if (addedOpen <= 0 || addedOpen != addedClose || !IsWellFormedNoBreak(japanese))
        {
            return actual;
        }
        return new Dictionary<string, int>(actual, StringComparer.Ordinal)
        {
            [NoBreakOpen] = actual[NoBreakOpen] - addedOpen,
            [NoBreakClose] = actual[NoBreakClose] - addedClose,
        };
    }

    /// <summary>
    /// &lt;nobr&gt; と &lt;/nobr&gt; が、入れ子なしで「開き → 閉じ」の順に交互に並び、最後に閉じているか。
    /// 深さを 0 と 1 だけで追い、それ以外になったら（入れ子・閉じが先）null にして不正とする。
    /// </summary>
    private static bool IsWellFormedNoBreak(string text) =>
        RichTextToken().Matches(text)
            .Select(m => m.Value)
            .Where(token => token is NoBreakOpen or NoBreakClose)
            .Aggregate((int?)0, (depth, token) => (depth, token) switch
            {
                (0, NoBreakOpen) => 1,
                (1, NoBreakClose) => 0,
                _ => null,
            }) == 0;
}
