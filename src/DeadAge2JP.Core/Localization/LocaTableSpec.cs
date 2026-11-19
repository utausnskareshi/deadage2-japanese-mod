namespace DeadAge2JP.Core.Localization;

/// <summary>
/// ゲーム内のローカライズ表（CSV 形式の TextAsset）1 つ分の定義。
/// </summary>
/// <param name="Id">翻訳ファイルで使う表の識別子（translations/{Id}/ フォルダ名にもなる）。</param>
/// <param name="AssetName">resources.assets 内の TextAsset 名。</param>
/// <param name="ContainerPath">
/// Resources.Load に渡されるパス（ResourceManager のコンテナキー）。
/// 同名の TextAsset が "backup/..." 側にも存在するため、名前ではなくこのパスで現行版を特定する。
/// </param>
/// <param name="SpeakerColumn">話者列の見出し（会話表のみ。翻訳作業の文脈情報として使う）。</param>
/// <param name="CommentColumn">開発者コメント列の見出し（会話表のみ）。</param>
public sealed record LocaTableSpec(
    string Id,
    string AssetName,
    string ContainerPath,
    string? SpeakerColumn,
    string? CommentColumn);

/// <summary>
/// Dead Age 2 に存在するローカライズ表の一覧。
/// </summary>
public static class LocaTableSpecs
{
    /// <summary>UI・アイテム・スキル等の文言（Localization.Get で参照される）。</summary>
    public static readonly LocaTableSpec Ui = new("ui", "Localization", "localization", null, null);

    /// <summary>会話・イベント文（SDDialogueManager が読む）。</summary>
    public static readonly LocaTableSpec Dialogue = new("dialogue", "DeadAge2LocaDialogue", "deadage2locadialogue", "Speaker", "DevComment");

    /// <summary>全ローカライズ表。</summary>
    public static readonly IReadOnlyList<LocaTableSpec> All = [Ui, Dialogue];

    /// <summary>識別子から表定義を引く（不正な識別子は意味のあるメッセージで例外）。</summary>
    public static LocaTableSpec FromId(string id) =>
        All.FirstOrDefault(s => s.Id == id)
        ?? throw new ArgumentException($"未知のローカライズ表です: '{id}'（有効値: {string.Join(", ", All.Select(s => s.Id))}）", nameof(id));
}
