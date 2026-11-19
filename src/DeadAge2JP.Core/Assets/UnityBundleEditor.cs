using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace DeadAge2JP.Core.Assets;

/// <summary>
/// Unity のアセットバンドル（UnityFS 形式。data.unity3d や AssetBundles/*）を読み込み、
/// 中のアセットを書き換えて別ファイルへ保存するための薄いラッパー。
/// <para>
/// ・圧縮（LZ4）されたバンドルは作業フォルダ上の一時ファイルへ展開して扱う（大容量でもメモリを圧迫しない）
/// ・型情報を持たないプレイヤーデータの MonoBehaviour は、ユーザー環境のゲーム DLL から型情報を生成して読む
///   （ゲーム由来の型情報を配布物に含めないため、また TMP のバージョン差を吸収するため）
/// ・元のバンドルファイルは読み取り専用で扱い、保存は必ず別パスへ行う
/// </para>
/// </summary>
public sealed class UnityBundleEditor : IDisposable
{
    private readonly AssetsManager _manager;
    private readonly BundleFileInstance _bundle;
    private readonly FileStream? _unpackedStream;

    /// <summary>元のバンドルファイルのストリーム（AssetsManager にも渡すが、確実に閉じるため自分でも持つ）。</summary>
    private readonly FileStream _sourceStream;

    /// <summary>
    /// MonoBehaviour の型情報をゲーム DLL から作る生成器。解放の仕方は <see cref="ReleaseGenerator"/> を参照。
    /// （IDisposable は実装していないので、as IDisposable では解放できない。具体的な型で持つ）
    /// </summary>
    private readonly MonoCecilTempGenerator _monoGenerator;

    private readonly string _workDir;
    private readonly string _bundleName;
    private readonly Dictionary<string, AssetsFileInstance> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _modifiedFiles = new(StringComparer.Ordinal);
    private bool _disposed;

    private UnityBundleEditor(
        AssetsManager manager,
        BundleFileInstance bundle,
        FileStream sourceStream,
        FileStream? unpackedStream,
        MonoCecilTempGenerator monoGenerator,
        string workDir,
        string bundleName)
    {
        _manager = manager;
        _bundle = bundle;
        _sourceStream = sourceStream;
        _unpackedStream = unpackedStream;
        _monoGenerator = monoGenerator;
        _workDir = workDir;
        _bundleName = bundleName;
    }

    /// <summary>
    /// バンドルを開く。
    /// </summary>
    /// <param name="bundlePath">対象バンドルのパス（読み取りのみ）。</param>
    /// <param name="classDataPath">Unity 組み込み型の型情報データベース（classdata.tpk）。</param>
    /// <param name="managedDir">ゲームの Managed フォルダ（MonoBehaviour の型情報生成に使う）。</param>
    /// <param name="workDir">展開用の一時ファイルを置く作業フォルダ。</param>
    public static UnityBundleEditor Open(string bundlePath, string classDataPath, string managedDir, string workDir)
    {
        if (!File.Exists(bundlePath))
        {
            throw new FileNotFoundException($"バンドルが見つかりません: {bundlePath}", bundlePath);
        }
        if (!File.Exists(classDataPath))
        {
            throw new FileNotFoundException($"型情報データベースが見つかりません: {classDataPath}", classDataPath);
        }
        if (!Directory.Exists(managedDir))
        {
            throw new DirectoryNotFoundException($"ゲームの Managed フォルダが見つかりません: {managedDir}");
        }
        Directory.CreateDirectory(workDir);

        var manager = new AssetsManager();
        var monoGenerator = new MonoCecilTempGenerator(managedDir);
        FileStream? source = null;
        FileStream? unpacked = null;
        try
        {
            manager.LoadClassPackage(classDataPath);
            manager.MonoTempGenerator = monoGenerator;
            // 元ファイルは自分で開いて渡す。壊れたバンドルで読み込みが例外になると AssetsManager はこのストリームを
            // 管理下に置かないため、パスで渡すと GC までゲームのファイルが開いたままになる（Steam の整合性確認で
            // 修復できなくなる）。自分で開いておけば、下の catch で確実に閉じられる
            source = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bundle = manager.LoadBundleFile(source, unpackIfPacked: false);
            if (bundle.file.DataIsCompressed)
            {
                // 圧縮バンドルは書き換えできないため、閉じると自動削除される一時ファイルへ展開する。
                // 展開後は元ファイルのハンドルが閉じられる（後で元ファイルを置き換えられるようにするため重要）。
                var unpackedPath = Path.Combine(workDir, $"{Path.GetFileName(bundlePath)}.{Guid.NewGuid():N}.unpacked");
                unpacked = new FileStream(unpackedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose);
                bundle.file = BundleHelper.UnpackBundleToStream(bundle.file, unpacked);
            }
            manager.LoadClassDatabaseFromPackage(bundle.file.Header.EngineVersion);
            return new UnityBundleEditor(manager, bundle, source, unpacked, monoGenerator, workDir, Path.GetFileName(bundlePath));
        }
        catch
        {
            // 途中で失敗した場合も開いたリソースを確実に解放してから例外を伝播する
            // （リゾルバーは UnloadAll が生成器の一覧を空にする前に集める。ReleaseGenerator を参照）
            var resolvers = ResolversOf(monoGenerator);
            manager.UnloadAll(true);
            unpacked?.Dispose();
            source?.Dispose();
            ReleaseGenerator(monoGenerator, resolvers);
            throw;
        }
    }

    /// <summary>バンドルを作った Unity のバージョン（例: 2021.3.36f1）。</summary>
    public string EngineVersion => _bundle.file.Header.EngineVersion;

    /// <summary>バンドル内のシリアライズファイル（.resS 等のストリーミングデータを除く）名の一覧。</summary>
    public IReadOnlyList<string> SerializedFileNames
    {
        get
        {
            ThrowIfDisposed();
            var file = _bundle.file;
            var names = new List<string>();
            for (var i = 0; i < file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                if (file.IsAssetsFile(i))
                {
                    names.Add(file.GetFileName(i));
                }
            }
            return names;
        }
    }

    /// <summary>バンドル内のシリアライズファイルを読み込む（同じファイルは 1 度だけ読む）。</summary>
    public AssetsFileInstance LoadSerializedFile(string name)
    {
        ThrowIfDisposed();
        if (_files.TryGetValue(name, out var cached))
        {
            return cached;
        }
        var index = _bundle.file.GetFileIndex(name);
        if (index < 0)
        {
            throw new InvalidDataException($"{_bundleName} の中に {name} が見つかりません。");
        }
        var instance = _manager.LoadAssetsFileFromBundle(_bundle, index, loadDeps: false);
        _files[name] = instance;
        return instance;
    }

    /// <summary>アセット名（m_Name）を高速に取得する（MonoBehaviour にも対応）。</summary>
    public string GetAssetName(AssetsFileInstance file, AssetFileInfo info)
    {
        ThrowIfDisposed();
        return AssetHelper.GetAssetNameFast(file.file, _manager.ClassDatabase, info);
    }

    /// <summary>アセット全体を読み込む（MonoBehaviour はゲーム DLL から生成した型情報で読む）。</summary>
    public AssetTypeValueField GetBaseField(AssetsFileInstance file, AssetFileInfo info)
    {
        ThrowIfDisposed();
        return _manager.GetBaseField(file, info);
    }

    /// <summary>
    /// 指定した配列フィールドを 1 つのバイト配列として読み込む。
    /// Font.m_FontData のような char 配列は通常 1 バイトごとに要素オブジェクトが作られ、
    /// 数 MB のフォントでは極端に遅く重くなるため、型情報の複製を書き換えてバイト配列扱いにする。
    /// </summary>
    public AssetTypeValueField GetBaseFieldWithByteArray(AssetsFileInstance file, AssetFileInfo info, string arrayFieldName)
    {
        ThrowIfDisposed();
        // キャッシュ済みの型情報を汚さないよう、必ず複製してから書き換える
        var template = _manager.GetTemplateBaseField(file, info).Clone();
        var arrayTemplate = template[arrayFieldName + ".Array"]
            ?? throw new InvalidDataException($"{GetAssetName(file, info)} に配列フィールド {arrayFieldName} がありません。");
        arrayTemplate.ValueType = AssetValueType.ByteArray;

        byte[] raw;
        lock (file.LockReader)
        {
            var reader = file.file.Reader;
            reader.Position = info.GetAbsoluteByteOffset(file.file);
            raw = reader.ReadBytes(checked((int)info.ByteSize));
        }
        using var stream = new MemoryStream(raw, writable: false);
        return template.MakeValue(new AssetsFileReader(stream), 0);
    }

    /// <summary>書き換えたアセットを登録する（保存時にバンドルへ反映される）。</summary>
    public void SetModified(AssetsFileInstance file, AssetFileInfo info, AssetTypeValueField baseField)
    {
        ThrowIfDisposed();
        info.SetNewData(baseField);
        var name = _files.FirstOrDefault(kv => ReferenceEquals(kv.Value, file)).Key
            ?? throw new InvalidOperationException("このエディタで読み込んでいないファイルのアセットは変更できません。");
        _modifiedFiles.Add(name);
    }

    /// <summary>変更済みのシリアライズファイルがあるか。</summary>
    public bool HasModifications => _modifiedFiles.Count > 0;

    /// <summary>
    /// 変更を反映したバンドルを LZ4 圧縮で outputPath へ保存する。
    /// 元ファイルと同じく「ブロック情報を先頭に置く」形式で書き出す。
    /// </summary>
    /// <param name="outputPath">保存先（元ファイルとは別のパスを指定すること）。</param>
    /// <param name="progress">0～1 の進捗（任意）。</param>
    public void Save(string outputPath, IProgress<double>? progress = null)
    {
        ThrowIfDisposed();
        foreach (var name in _modifiedFiles)
        {
            var index = _bundle.file.GetFileIndex(name);
            _bundle.file.BlockAndDirInfo.DirectoryInfos[index].SetNewData(_files[name].file);
        }

        // 1) 変更を反映した非圧縮バンドルを作業フォルダへ書き出す
        var modifiedPath = Path.Combine(_workDir, $"{_bundleName}.{Guid.NewGuid():N}.modified");
        try
        {
            using (var writer = new AssetsFileWriter(modifiedPath))
            {
                _bundle.file.Write(writer);
            }
            progress?.Report(0.3);

            // 2) それを LZ4 で圧縮して出力する
            using var rawStream = new FileStream(modifiedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var raw = new AssetBundleFile();
            raw.Read(new AssetsFileReader(rawStream));
            using (var packWriter = new AssetsFileWriter(outputPath))
            {
                raw.Pack(packWriter, AssetBundleCompressionType.LZ4Fast, blockDirAtEnd: false,
                    new CompressProgress(p => progress?.Report(0.3 + 0.7 * p)));
            }
            raw.Close();
        }
        finally
        {
            if (File.Exists(modifiedPath))
            {
                File.Delete(modifiedPath);
            }
        }
        progress?.Report(1.0);
    }

    /// <summary>読み込んだファイル・一時ファイルをすべて解放する。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // リゾルバーは UnloadAll が生成器の一覧を空にする前に集める（ReleaseGenerator を参照）
        var resolvers = ResolversOf(_monoGenerator);
        _manager.UnloadAll(true);
        _unpackedStream?.Dispose();
        _sourceStream.Dispose();
        ReleaseGenerator(_monoGenerator, resolvers);
    }

    /// <summary>
    /// 生成器が読み込んだアセンブリごとのリゾルバー（依存 DLL を開いたまま保持している）を集める。
    /// AssetsManager.UnloadAll は、インターフェース（IMonoBehaviourTemplateGenerator.Dispose）経由で
    /// 生成器を解放して一覧を空にするため、必ず UnloadAll より前に呼ぶこと。
    /// </summary>
    private static List<Mono.Cecil.IAssemblyResolver> ResolversOf(MonoCecilTempGenerator generator) =>
        generator.loadedAssemblies.Values
            .Select(assembly => assembly.MainModule.AssemblyResolver)
            .Distinct()
            .ToList();

    /// <summary>
    /// 型情報の生成器を解放し、ゲームの Managed フォルダの DLL を確実に手放す。
    /// <para>
    /// 生成器の Dispose()（UnloadAll からも呼ばれる）は、直接読み込んだアセンブリ（Unity.TextMeshPro.dll 等）しか閉じない。
    /// 型を解決するときに依存関係として読み込まれた DLL（mscorlib.dll・UnityEngine.CoreModule.dll など）は、
    /// アセンブリごとに作られたリゾルバーのキャッシュが開いたまま保持し、GC まで閉じられない。
    /// 失敗の直後に Steam の整合性確認を案内しても、そのファイルを修復できない恐れがあるため、リゾルバーも閉じる。
    /// </para>
    /// </summary>
    /// <param name="generator">解放する生成器（UnloadAll が先に解放済みでも、二重に呼んで問題ない）。</param>
    /// <param name="resolvers">UnloadAll より前に <see cref="ResolversOf"/> で集めたリゾルバー。</param>
    private static void ReleaseGenerator(MonoCecilTempGenerator generator, IEnumerable<Mono.Cecil.IAssemblyResolver> resolvers)
    {
        generator.Dispose();
        foreach (var resolver in resolvers)
        {
            resolver.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>AssetsTools.NET の圧縮進捗をデリゲートへ橋渡しする。</summary>
    private sealed class CompressProgress(Action<double> report) : IAssetBundleCompressProgress
    {
        public void SetProgress(float progress) => report(progress);
    }
}
