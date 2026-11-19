namespace DeadAge2JP.Core.Patching;

/// <summary>
/// 適用記録と現在のファイルハッシュから状態を判定する純粋関数。
/// </summary>
public static class PatchStatusEvaluator
{
    /// <summary>
    /// 状態を判定する。
    /// </summary>
    /// <param name="state">適用記録（無ければ null）。</param>
    /// <param name="currentHashes">相対パス → 現在の SHA-256。</param>
    public static PatchStatus Evaluate(PatchState? state, IReadOnlyDictionary<string, string> currentHashes)
    {
        ArgumentNullException.ThrowIfNull(currentHashes);
        if (state is null || state.Files.Count == 0)
        {
            return PatchStatus.NotPatched;
        }

        var patched = 0;
        var original = 0;
        foreach (var file in state.Files)
        {
            var current = currentHashes.GetValueOrDefault(file.RelativePath);
            if (current == file.PatchedSha256)
            {
                patched++;
            }
            else if (current == file.OriginalSha256)
            {
                original++;
            }
        }

        if (patched == state.Files.Count)
        {
            return PatchStatus.Patched;
        }
        if (patched > 0)
        {
            return PatchStatus.PartiallyPatched;
        }
        // 全て元のまま（Steam の整合性確認で戻された等）なら単に未適用
        return original == state.Files.Count ? PatchStatus.NotPatched : PatchStatus.GameFilesChanged;
    }
}
