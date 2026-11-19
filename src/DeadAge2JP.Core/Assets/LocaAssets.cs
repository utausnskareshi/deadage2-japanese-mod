using AssetsTools.NET;
using AssetsTools.NET.Extra;
using DeadAge2JP.Core.Localization;

namespace DeadAge2JP.Core.Assets;

/// <summary>バンドル内のアセットの所在（シリアライズファイル名 + PathID）。</summary>
public sealed record AssetLocation(string FileName, long PathId);

/// <summary>
/// data.unity3d 内のローカライズ表（TextAsset）を特定・読み書きする。
/// </summary>
public static class LocaAssets
{
    /// <summary>ResourceManager を持つシリアライズファイル名。</summary>
    private const string GlobalGameManagers = "globalgamemanagers";

    /// <summary>
    /// ResourceManager のコンテナ（Resources.Load のパス → アセット）を読み、指定パスの所在を返す。
    /// ゲームは Resources.Load("Localization") 等で表を読むため、コンテナで引けば現行版を確実に特定できる
    /// （同名の旧版 TextAsset が "backup/..." に存在するので、名前で探すと取り違える）。
    /// </summary>
    public static AssetLocation FindResource(UnityBundleEditor editor, string containerPath)
    {
        var ggm = editor.LoadSerializedFile(GlobalGameManagers);
        var managerInfo = ggm.file.GetAssetsOfType(AssetClassID.ResourceManager).SingleOrDefault()
            ?? throw new InvalidDataException("ResourceManager が見つかりません。ゲームのデータ形式が想定と異なります。");
        var container = editor.GetBaseField(ggm, managerInfo)["m_Container.Array"];

        var matches = new List<AssetLocation>();
        foreach (var pair in container.Children)
        {
            if (!string.Equals(pair["first"].AsString, containerPath, StringComparison.Ordinal))
            {
                continue;
            }
            var pointer = pair["second"];
            var fileId = pointer["m_FileID"].AsInt;
            var pathId = pointer["m_PathID"].AsLong;
            var fileName = fileId == 0
                ? GlobalGameManagers
                : Path.GetFileName(ggm.file.Metadata.Externals[fileId - 1].PathName);
            matches.Add(new AssetLocation(fileName, pathId));
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException($"リソース '{containerPath}' が見つかりません。ゲームのバージョンが想定と異なる可能性があります。"),
            _ => throw new InvalidDataException($"リソース '{containerPath}' が複数見つかりました（{matches.Count} 件）。特定できません。"),
        };
    }

    /// <summary>ローカライズ表の CSV バイト列を読む。</summary>
    public static byte[] ReadTable(UnityBundleEditor editor, LocaTableSpec spec) =>
        Load(editor, spec).Field["m_Script"].AsByteArray;

    /// <summary>ローカライズ表の CSV バイト列を書き換える（保存は editor.Save で行う）。</summary>
    public static void WriteTable(UnityBundleEditor editor, LocaTableSpec spec, byte[] csvBytes)
    {
        var (file, info, field) = Load(editor, spec);
        field["m_Script"].AsByteArray = csvBytes;
        editor.SetModified(file, info, field);
    }

    /// <summary>表の TextAsset を読み込み、名前が想定どおりかを検証する。</summary>
    private static (AssetsFileInstance File, AssetFileInfo Info, AssetTypeValueField Field) Load(UnityBundleEditor editor, LocaTableSpec spec)
    {
        var location = FindResource(editor, spec.ContainerPath);
        var file = editor.LoadSerializedFile(location.FileName);
        var info = file.file.GetAssetInfo(location.PathId)
            ?? throw new InvalidDataException($"{location.FileName} に PathID {location.PathId} のアセットがありません。");
        if (info.TypeId != (int)AssetClassID.TextAsset)
        {
            throw new InvalidDataException($"リソース '{spec.ContainerPath}' が TextAsset ではありません（型 ID {info.TypeId}）。");
        }
        var field = editor.GetBaseField(file, info);
        var name = field["m_Name"].AsString;
        if (!string.Equals(name, spec.AssetName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"リソース '{spec.ContainerPath}' の名前が想定外です（期待: {spec.AssetName}、実際: {name}）。");
        }
        return (file, info, field);
    }
}
