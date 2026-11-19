using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeadAge2JP.Core.Settings;

/// <summary>
/// ゲームの設定ファイル（settings.json）内の言語設定（gameLanguage）を読み書きする。
/// <para>
/// 設定ファイルは FullSerializer が出力した JSON（UTF-16）。書式を崩さないよう、
/// JSON を再構築せず gameLanguage の値だけを置換する。値は列挙名（"English"）または数値（20）の両方に対応する。
/// </para>
/// </summary>
public static partial class GameSettingsLanguage
{
    [GeneratedRegex("(?<prefix>\"gameLanguage\"\\s*:\\s*)(?:\"(?<name>[A-Za-z_]+)\"|(?<number>-?\\d+))", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();

    /// <summary>
    /// 言語名として書き込める文字列（読み取り側の name と同じ文字種）。
    /// 末尾は $ ではなく \z で判定する（$ は末尾の改行の直前にも一致するため、"English\n" を通して
    /// 生の改行を含む不正な JSON を書いてしまう。改ざんされた適用記録の元の言語名から到達できる）。
    /// </summary>
    [GeneratedRegex(@"\A[A-Za-z_]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageName();

    /// <summary>
    /// 設定 JSON から言語名を読む（数値なら列挙名へ変換。項目が無ければ null）。
    /// 名前で保存されている場合は、本ツールの一覧に無い言語（ゲーム更新で増えた言語など）もそのまま返す。
    /// </summary>
    /// <exception cref="InvalidDataException">数値が一覧に無い、または int に収まらない場合。</exception>
    public static string? ReadLanguage(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var match = LanguagePattern().Match(json);
        if (!match.Success)
        {
            return null;
        }
        if (match.Groups["name"].Success)
        {
            return match.Groups["name"].Value;
        }
        // 桁あふれ（OverflowException）も呼び出し側が想定する InvalidDataException にそろえる
        var text = match.Groups["number"].Value;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidDataException($"言語番号を読めません: {text}");
        }
        return GameConstants.BountyLanguageValues.FirstOrDefault(kv => kv.Value == number).Key
            ?? throw new InvalidDataException($"未知の言語番号です: {number}");
    }

    /// <summary>
    /// 設定 JSON の言語を書き換えた新しい文字列を返す（元の表記形式＝名前/数値を維持する）。
    /// <para>
    /// 名前で保存されている場合は、一覧に無い言語名もそのまま書く。元に戻すときは「適用前に設定ファイルから
    /// 読んだ名前」を書き戻すので、ゲーム更新で増えた言語でも元どおりにできる（一覧で拒否すると、元に戻す処理が
    /// 毎回ここで失敗し、ゲームが中国語枠のまま残ってしまう）。
    /// 数値で保存されている場合は番号が必要なので、一覧にある言語だけ書ける。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">言語名に使えない文字を含む場合。</exception>
    /// <exception cref="InvalidDataException">gameLanguage の項目が無い、または数値形式で番号が分からない場合。</exception>
    public static string WithLanguage(string json, string languageName)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(languageName);
        if (!LanguageName().IsMatch(languageName))
        {
            throw new ArgumentException($"言語名として不正です: {languageName}", nameof(languageName));
        }
        var match = LanguagePattern().Match(json);
        if (!match.Success)
        {
            throw new InvalidDataException("設定ファイルに gameLanguage の項目がありません。");
        }

        var prefix = match.Groups["prefix"].Value;
        string replacement;
        if (match.Groups["name"].Success)
        {
            replacement = $"{prefix}\"{languageName}\"";
        }
        else if (GameConstants.BountyLanguageValues.TryGetValue(languageName, out var number))
        {
            replacement = prefix + number.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            throw new InvalidDataException($"言語 {languageName} の番号が分からないため、設定ファイルに書き込めません。");
        }
        return string.Concat(json.AsSpan(0, match.Index), replacement, json.AsSpan(match.Index + match.Length));
    }
}

/// <summary>
/// settings.json ファイルの入出力（ゲームと同じ UTF-16 LE / BOM 付きで読み書きする）。
/// </summary>
public static class GameSettingsFile
{
    /// <summary>設定ファイルの言語を読む（ファイルが無ければ null）。</summary>
    public static string? TryReadLanguage(string path) =>
        File.Exists(path) ? GameSettingsLanguage.ReadLanguage(File.ReadAllText(path, Encoding.Unicode)) : null;

    /// <summary>設定ファイルの言語を書き換える（途中で壊れないよう一時ファイル経由で置換）。</summary>
    public static void WriteLanguage(string path, string languageName)
    {
        var json = File.ReadAllText(path, Encoding.Unicode);
        var updated = GameSettingsLanguage.WithLanguage(json, languageName);
        var temp = path + ".jptmp";
        File.WriteAllText(temp, updated, Encoding.Unicode);
        File.Move(temp, path, overwrite: true);
    }
}
