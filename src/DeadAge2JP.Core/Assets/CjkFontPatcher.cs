using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace DeadAge2JP.Core.Assets;

/// <summary>フォント書き換えの集計。</summary>
/// <param name="FontsReplaced">中身を日本語フォントに差し替えた Font アセット数。</param>
/// <param name="DynamicAssetsUpdated">マルチアトラスを有効化した動的 TMP フォントアセット数。</param>
/// <param name="StaticAssetsCleared">文字表を空にした静的 TMP フォントアセット数。</param>
public sealed record FontPatchReport(int FontsReplaced, int DynamicAssetsUpdated, int StaticAssetsCleared);

/// <summary>検証用に読み出したフォント関連アセットの状態。</summary>
/// <param name="FontDataLength">CJK フォント（Font アセット）のデータ長（バンドル内に無ければ null）。</param>
/// <param name="DynamicMultiAtlas">動的アセット名 → マルチアトラス有効か。</param>
/// <param name="CharacterCounts">TMP アセット名 → 文字表の件数。</param>
public sealed record FontStateSnapshot(
    long? FontDataLength,
    IReadOnlyDictionary<string, bool> DynamicMultiAtlas,
    IReadOnlyDictionary<string, int> CharacterCounts);

/// <summary>
/// 中国語用に同梱された CJK フォント一式を、日本語表示に適した状態へ書き換える。
/// <para>
/// ゲームの UI フォント（TextMeshPro）は、足りない文字を最終的に
/// 「wqy-microhei 静的アセット（簡体字 2,205 字）」→「wqy-microhei 動的アセット（実行時生成）」で補う構成になっている。
/// </para>
/// <list type="number">
/// <item>動的アセット: マルチアトラスを有効化（1024×1024 一枚では日本語の字数で溢れて文字が欠けるため）</item>
/// <item>（日本語フォント指定時）Font アセット wqy-microhei の中身を日本語フォントに差し替え</item>
/// <item>（日本語フォント指定時）静的・動的アセットの焼き込み済み文字表を空にし、すべて日本語フォントから生成させる</item>
/// </list>
/// 行間などの縦方向メトリクス（FaceInfo）は、中国語版で調整済みのレイアウトを保つため変更しない。
/// </summary>
public static class CjkFontPatcher
{
    /// <summary>
    /// バンドル内の全シリアライズファイルを走査して書き換える。
    /// 想定したアセットが見つからない場合は、部分的な適用を避けるため例外にする。
    /// </summary>
    /// <param name="editor">対象バンドル。</param>
    /// <param name="replacementFontData">差し替える日本語フォント（TTF/OTF）のバイト列。null ならフォントは差し替えない。</param>
    public static FontPatchReport Apply(UnityBundleEditor editor, byte[]? replacementFontData)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var fontsReplaced = 0;
        var dynamicUpdated = new HashSet<string>(StringComparer.Ordinal);
        var staticCleared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fileName in editor.SerializedFileNames)
        {
            var file = editor.LoadSerializedFile(fileName);

            if (replacementFontData is not null)
            {
                foreach (var info in file.file.GetAssetsOfType(AssetClassID.Font))
                {
                    if (editor.GetAssetName(file, info) != GameConstants.CjkFontAssetName)
                    {
                        continue;
                    }
                    var field = editor.GetBaseFieldWithByteArray(file, info, "m_FontData");
                    field["m_FontData.Array"].AsByteArray = replacementFontData;
                    editor.SetModified(file, info, field);
                    fontsReplaced++;
                }
            }

            foreach (var info in file.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var name = editor.GetAssetName(file, info);
                var isDynamic = GameConstants.DynamicCjkFontAssetNames.Contains(name);
                var isStatic = GameConstants.StaticCjkFontAssetNames.Contains(name);
                if (!isDynamic && !(isStatic && replacementFontData is not null))
                {
                    continue;
                }

                var field = editor.GetBaseField(file, info);
                if (isDynamic)
                {
                    Require(field, "m_IsMultiAtlasTexturesEnabled", name).AsBool = true;
                    dynamicUpdated.Add(name);
                }
                if (replacementFontData is not null)
                {
                    ClearGlyphTables(field, name);
                    if (isStatic)
                    {
                        staticCleared.Add(name);
                    }
                }
                editor.SetModified(file, info, field);
            }
        }

        // 1 つでも欠けていれば、ゲームのバージョン違い等で想定が崩れているので中断する
        var missingDynamic = GameConstants.DynamicCjkFontAssetNames.Except(dynamicUpdated).ToList();
        if (missingDynamic.Count > 0)
        {
            throw new InvalidDataException($"動的フォントアセットが見つかりません: {string.Join(", ", missingDynamic)}");
        }
        if (replacementFontData is not null)
        {
            if (fontsReplaced == 0)
            {
                throw new InvalidDataException($"フォント '{GameConstants.CjkFontAssetName}' が見つかりません。");
            }
            var missingStatic = GameConstants.StaticCjkFontAssetNames.Except(staticCleared).ToList();
            if (missingStatic.Count > 0)
            {
                throw new InvalidDataException($"静的フォントアセットが見つかりません: {string.Join(", ", missingStatic)}");
            }
        }

        return new FontPatchReport(fontsReplaced, dynamicUpdated.Count, staticCleared.Count);
    }

    /// <summary>検証用に、CJK フォント関連アセットの現在の状態を読み出す。</summary>
    public static FontStateSnapshot ReadState(UnityBundleEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        long? fontLength = null;
        var multiAtlas = new Dictionary<string, bool>(StringComparer.Ordinal);
        var characterCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var tmpNames = GameConstants.DynamicCjkFontAssetNames.Concat(GameConstants.StaticCjkFontAssetNames).ToHashSet(StringComparer.Ordinal);

        foreach (var fileName in editor.SerializedFileNames)
        {
            var file = editor.LoadSerializedFile(fileName);
            foreach (var info in file.file.GetAssetsOfType(AssetClassID.Font))
            {
                if (editor.GetAssetName(file, info) == GameConstants.CjkFontAssetName)
                {
                    fontLength = editor.GetBaseFieldWithByteArray(file, info, "m_FontData")["m_FontData.Array"].AsByteArray.LongLength;
                }
            }
            foreach (var info in file.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var name = editor.GetAssetName(file, info);
                if (!tmpNames.Contains(name))
                {
                    continue;
                }
                var field = editor.GetBaseField(file, info);
                characterCounts[name] = Require(field, "m_CharacterTable.Array", name).Children.Count;
                if (GameConstants.DynamicCjkFontAssetNames.Contains(name))
                {
                    multiAtlas[name] = Require(field, "m_IsMultiAtlasTexturesEnabled", name).AsBool;
                }
            }
        }
        return new FontStateSnapshot(fontLength, multiAtlas, characterCounts);
    }

    /// <summary>
    /// 焼き込み済みの文字表・グリフ表・カーニング表を空にする。
    /// グリフ番号は元フォント（wqy）基準なので、フォントを差し替えたら必ず捨てる必要がある。
    /// アトラス上の使用領域（m_UsedGlyphRects 等）は残すため、既存の画素が上書きされることはない。
    /// </summary>
    private static void ClearGlyphTables(AssetTypeValueField field, string assetName)
    {
        Require(field, "m_CharacterTable.Array", assetName).Children.Clear();
        Require(field, "m_GlyphTable.Array", assetName).Children.Clear();
        Require(field, "m_FontFeatureTable.m_GlyphPairAdjustmentRecords.Array", assetName).Children.Clear();
    }

    /// <summary>フィールドを取得し、存在しなければ原因の分かる例外にする。</summary>
    private static AssetTypeValueField Require(AssetTypeValueField field, string path, string assetName)
    {
        var child = field[path];
        if (child.IsDummy)
        {
            throw new InvalidDataException($"TMP フォントアセット '{assetName}' にフィールド {path} がありません（TextMeshPro のバージョンが想定と異なります）。");
        }
        return child;
    }
}
