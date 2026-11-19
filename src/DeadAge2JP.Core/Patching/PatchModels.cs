namespace DeadAge2JP.Core.Patching;

/// <summary>
/// 利用者に表示すべき「想定内の失敗」（ゲーム起動中、権限不足、ファイル不足など）。
/// メッセージはそのまま画面に出せる日本語で書く。
/// </summary>
public sealed class PatchException : Exception
{
    public PatchException(string message) : base(message)
    {
    }

    public PatchException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>管理者権限で再実行すれば解決する可能性が高い失敗か。</summary>
    public bool MayNeedElevation { get; init; }

    /// <summary>
    /// ゲームのファイルが中途半端な状態の可能性があり、バックアップを消してはいけない失敗か
    /// （置き換えの自動復旧にも失敗した場合）。
    /// </summary>
    public bool KeepBackup { get; init; }
}

/// <summary>処理の進捗（画面表示用）。</summary>
/// <param name="Message">現在の作業内容。</param>
/// <param name="Fraction">全体に対する進捗（0～1）。</param>
public sealed record PatchProgress(string Message, double Fraction);

/// <summary>パッチ処理のログ出力先。</summary>
public interface IPatchLogger
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

/// <summary>
/// 本ツールが %LOCALAPPDATA% に置くファイルの場所。
/// </summary>
/// <param name="RootDir">ルート（既定: %LOCALAPPDATA%\DeadAge2JP）。</param>
public sealed record AppPaths(string RootDir)
{
    /// <summary>既定の場所。</summary>
    public static AppPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadAge2JP"));

    /// <summary>パッチ適用状態の記録。</summary>
    public string StateFile => Path.Combine(RootDir, "state.json");

    /// <summary>元ファイルのバックアップ置き場。</summary>
    public string BackupRoot => Path.Combine(RootDir, "backup");

    /// <summary>展開・圧縮用の一時作業フォルダ。</summary>
    public string WorkDir => Path.Combine(RootDir, "work");

    /// <summary>ログフォルダ。</summary>
    public string LogDir => Path.Combine(RootDir, "logs");

    /// <summary>適用・元に戻すの同時実行を防ぐロックファイル（処理中だけ存在する）。</summary>
    public string LockFile => Path.Combine(RootDir, "patcher.lock");

    /// <summary>
    /// ゲームの設定ファイル（settings.json）の場所を差し替える（テスト用。通常は null のまま）。
    /// 指定しないと、本物の利用者の設定ファイル（%USERPROFILE%\AppData\LocalLow\...）を読み書きする。
    /// </summary>
    public string? GameSettingsFileOverride { get; init; }

    /// <summary>ゲームの設定ファイル（settings.json）のパス。</summary>
    public string GameSettingsFilePath => GameSettingsFileOverride ?? GameConstants.GetSettingsFilePath();
}

/// <summary>パッチ適用の選択肢。</summary>
/// <param name="ReplaceFont">同梱の中国語フォントを日本語フォントに差し替えるか。</param>
/// <param name="SwitchGameLanguage">設定ファイルがあれば言語を日本語（中国語枠）へ自動で切り替えるか。</param>
public sealed record ApplyOptions(bool ReplaceFont = true, bool SwitchGameLanguage = true);

/// <summary>パッチ適用対象ファイル 1 つ分の記録。</summary>
/// <param name="RelativePath">ゲームフォルダからの相対パス。</param>
/// <param name="OriginalSha256">適用前のハッシュ。</param>
/// <param name="PatchedSha256">適用後のハッシュ。</param>
/// <param name="BackupPath">元ファイルのバックアップの場所。</param>
public sealed record PatchedFileRecord(string RelativePath, string OriginalSha256, string PatchedSha256, string BackupPath);

/// <summary>設定ファイルの言語を切り替えた記録（元に戻すときに使う）。</summary>
public sealed record SettingsChangeRecord(string SettingsPath, string OriginalLanguage);

/// <summary>翻訳の適用件数（表ごと）。</summary>
/// <param name="TableId">表 ID（ui / dialogue）。</param>
/// <param name="KeyedRows">キーを持つ行の総数。</param>
/// <param name="Translated">日本語訳を入れた行数。</param>
/// <param name="StaleTranslations">翻訳時から英語原文が変わっている行数。</param>
/// <param name="UnknownKeys">訳文側にあるがゲームの表に無いキーの数。</param>
/// <param name="EmptySourceRows">
/// 英語原文が空で訳すものが無い行数。この項目が無い古い適用記録（v0.1.0 の初期版）を読んだ場合は 0 になり、
/// 日本語化率は従来どおりの表示になる（記録の形式は互換のため、版は上げない）。
/// </param>
public sealed record TableSummary(
    string TableId,
    int KeyedRows,
    int Translated,
    int StaleTranslations,
    int UnknownKeys,
    int EmptySourceRows = 0)
{
    /// <summary>
    /// 日本語化率の分母（訳す必要のある行数＝キーのある行から原文が空の行を除いた数）。
    /// 改ざんされた記録で負になっても表示が崩れないよう 0 未満にはしない。記録には保存しない計算値。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int TranslatableRows => Math.Max(0, KeyedRows - EmptySourceRows);
}

/// <summary>
/// パッチ適用状態。%LOCALAPPDATA% に保存し、ゲームフォルダにも写しを置く。
/// </summary>
public sealed record PatchState(
    int SchemaVersion,
    string PatcherVersion,
    DateTimeOffset AppliedAt,
    string GameRoot,
    string? BuildId,
    string BackupDir,
    bool FontReplaced,
    IReadOnlyList<PatchedFileRecord> Files,
    SettingsChangeRecord? Settings,
    IReadOnlyList<TableSummary> Tables)
{
    /// <summary>現在の状態ファイル形式のバージョン。</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>ゲームファイルの現在の状態。</summary>
public enum PatchStatus
{
    /// <summary>未適用（ボタンは「日本語化する」）。</summary>
    NotPatched,

    /// <summary>全ファイル適用済み（ボタンは「元に戻す」）。</summary>
    Patched,

    /// <summary>一部のファイルだけ適用済み（ゲーム更新で一部が上書きされた等。「元に戻す」で残りを戻せる）。</summary>
    PartiallyPatched,

    /// <summary>記録はあるが、ファイルが適用前とも適用後とも違う（ゲーム更新）。再適用が必要。</summary>
    GameFilesChanged,
}

/// <summary>状態判定の結果。</summary>
/// <param name="Status">判定された状態。</param>
/// <param name="State">読み込んだ適用記録（無ければ null）。</param>
public sealed record PatchStatusReport(PatchStatus Status, PatchState? State);

/// <summary>パッチ適用の結果。</summary>
public sealed record ApplyOutcome(PatchState State, IReadOnlyList<string> Notes);

/// <summary>元に戻した結果。</summary>
public sealed record RestoreOutcome(IReadOnlyList<string> Notes);
