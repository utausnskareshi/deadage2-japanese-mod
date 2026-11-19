namespace DeadAge2JP.Core.Steam;

/// <summary>
/// 検出した Dead Age 2 のインストール情報。
/// </summary>
/// <param name="RootDir">ゲームフォルダ（DeadAge2.exe があるフォルダ）。</param>
/// <param name="BuildId">Steam のビルド ID（appmanifest の buildid。不明なら null）。</param>
/// <param name="BetaBranch">選択中のベータブランチ名（公開版なら null）。</param>
/// <param name="StateFlags">appmanifest の StateFlags（4 = 完全にインストール済み）。</param>
public sealed record GameInstallation(string RootDir, string? BuildId, string? BetaBranch, int? StateFlags)
{
    /// <summary>テキストと UI フォントを含むバンドルのフルパス。</summary>
    public string DataBundlePath => Path.Combine(RootDir, GameConstants.DataBundleRelativePath);

    /// <summary>GUI プレハブとフォントの複製を含むバンドルのフルパス。</summary>
    public string PrefabsBundlePath => Path.Combine(RootDir, GameConstants.PrefabsBundleRelativePath);

    /// <summary>ゲームの Managed フォルダ。</summary>
    public string ManagedDir => Path.Combine(RootDir, GameConstants.ManagedDirRelativePath);

    /// <summary>Steam 上で更新待ち・ダウンロード途中など、完全にインストールされた状態でないか。</summary>
    public bool HasPendingSteamUpdate => StateFlags is int flags && flags != 4;

    /// <summary>パッチで書き換える対象ファイル（ゲームフォルダからの相対パス）。</summary>
    public static IReadOnlyList<string> PatchTargets { get; } =
        [GameConstants.DataBundleRelativePath, GameConstants.PrefabsBundleRelativePath];

    /// <summary>
    /// 必須ファイルがそろっているかを確認し、足りないもの（相対パス）を返す。空ならゲームとして有効。
    /// </summary>
    public static IReadOnlyList<string> FindMissingFiles(string rootDir)
    {
        string[] required =
        [
            GameConstants.ExecutableName,
            GameConstants.DataBundleRelativePath,
            GameConstants.PrefabsBundleRelativePath,
            Path.Combine(GameConstants.ManagedDirRelativePath, "Assembly-CSharp.dll"),
            Path.Combine(GameConstants.ManagedDirRelativePath, "Unity.TextMeshPro.dll"),
        ];
        return required.Where(relative => !File.Exists(Path.Combine(rootDir, relative))).ToList();
    }
}
