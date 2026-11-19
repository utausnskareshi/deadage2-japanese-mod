namespace DeadAge2JP.Core;

/// <summary>
/// Dead Age 2（Steam版）に関する固定値。
/// ゲームの解析（2026-11 時点、build 20299076 / Unity 2021.3.36f1）で確認した事実をここに集約し、
/// ほかのコードにマジックナンバーや文字列を散らさないようにする。
/// </summary>
public static class GameConstants
{
    /// <summary>Steam の AppID。</summary>
    public const string SteamAppId = "951430";

    /// <summary>ゲーム本体の実行ファイル名（インストール検出の確認に使う）。</summary>
    public const string ExecutableName = "DeadAge2.exe";

    /// <summary>起動中判定に使うプロセス名（拡張子なし）。</summary>
    public const string ProcessName = "DeadAge2";

    /// <summary>テキストと UI フォントを含むプレイヤーデータバンドル（ゲームフォルダからの相対パス）。</summary>
    public static readonly string DataBundleRelativePath = Path.Combine("DeadAge2_Data", "data.unity3d");

    /// <summary>ゲーム内 GUI のプレハブとフォントの複製を含むアセットバンドル（ゲームフォルダからの相対パス）。</summary>
    public static readonly string PrefabsBundleRelativePath = Path.Combine("AssetBundles", "prefabs");

    /// <summary>MonoBehaviour の型情報生成に使うマネージド DLL のフォルダ（ゲームフォルダからの相対パス）。</summary>
    public static readonly string ManagedDirRelativePath = Path.Combine("DeadAge2_Data", "Managed");

    /// <summary>パッチ状態のミラーを置く場所（ゲームフォルダからの相対パス）。</summary>
    public static readonly string StateMirrorRelativePath = Path.Combine("DeadAge2_Data", "DeadAge2JP.state.json");

    /// <summary>日本語で置き換える言語列（ゲーム内の BountyLanguage 列挙名と CSV 見出しが一致する）。</summary>
    public const string TargetLanguageColumn = "Chinese_Simplified";

    /// <summary>未訳時のコピー元・フォールバック先となる英語列の見出し。</summary>
    public const string EnglishColumn = "English";

    /// <summary>言語選択ドロップダウンに表示される、置き換え対象言語の名前キー。</summary>
    public const string LanguageLabelKey = "Language_" + TargetLanguageColumn;

    /// <summary>言語選択ドロップダウンに表示する日本語の言語名（全言語列に設定する）。</summary>
    public const string JapaneseLanguageLabel = "日本語 (JA)";

    /// <summary>CJK 表示に使われている同梱フォント（Font アセット名）。日本語フォントに中身を差し替える。</summary>
    public const string CjkFontAssetName = "wqy-microhei";

    /// <summary>
    /// 実行時に文字を描き足す動的 TMP フォントアセット名。
    /// 単一アトラス（1024×1024・約300字）では日本語で溢れるため、マルチアトラスを有効化する。
    /// </summary>
    public static readonly IReadOnlyList<string> DynamicCjkFontAssetNames =
    [
        "wqy-microhei SDF_dynamic",
        "wqy-microhei_shadow SDF_dynamic",
        "wqy-microhei_outline_dynamic",
    ];

    /// <summary>
    /// 簡体字 2,205 字を焼き込んだ静的 TMP フォントアセット名。
    /// 日本語フォントへ差し替える場合は、中国字形で描かれないよう文字表を空にして動的アセットへ回す。
    /// </summary>
    public static readonly IReadOnlyList<string> StaticCjkFontAssetNames =
    [
        "wqy-microhei SDF_static",
        "wqy-microhei_shadow SDF_static",
        "wqy-microhei_outline_static",
    ];

    /// <summary>
    /// ゲームの言語列挙（BountyLanguage）の名前と数値。settings.json が数値で保存されている場合の変換に使う。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> BountyLanguageValues = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["None"] = 0,
        ["German"] = 10,
        ["English"] = 20,
        ["French"] = 30,
        ["Spanish_Latin"] = 40,
        ["Spanish_Spain"] = 41,
        ["Portuguese_Brazil"] = 50,
        ["Russian"] = 60,
        ["Chinese_Simplified"] = 70,
        ["Italian"] = 80,
        ["Polish"] = 90,
    };

    /// <summary>
    /// ゲームの設定ファイル（settings.json, UTF-16）のパス。
    /// Unity の Application.persistentDataPath = %USERPROFILE%\AppData\LocalLow\{会社名}\{製品名}。
    /// </summary>
    public static string GetSettingsFilePath()
    {
        // LocalLow は SpecialFolder に無いため、Local の親フォルダから組み立てる
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataRoot = Path.GetDirectoryName(localAppData)
            ?? throw new InvalidOperationException($"AppData フォルダを特定できません: {localAppData}");
        return Path.Combine(appDataRoot, "LocalLow", "SilentDreams", "DeadAge2", "settings.json");
    }
}
