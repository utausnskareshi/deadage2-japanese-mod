using System.Text;

namespace DeadAge2JP.Core.Csv;

/// <summary>
/// ゲーム側 ByteReader.ReadCSV（NGUI 由来の独自 CSV 方言）を忠実に移植した読み取り器。
/// <para>
/// 一般的な CSV と異なる点（すべてゲームと同じ挙動にしている）:
/// ・行頭の制御文字（コード 32 未満）は空行扱いで読み飛ばす
/// ・行内の 2 文字 "\n"（バックスラッシュ + n）は改行文字に変換される
/// ・ダブルクォートはフィールドの途中でも引用開始とみなされる
/// ・行末がカンマで終わる場合、末尾の空フィールドは結果に含まれない
/// </para>
/// ゲームが読んだ結果と 1 セル単位で一致させることが目的なので、挙動を「改善」しないこと。
/// </summary>
public static class NguiCsvReader
{
    /// <summary>
    /// バイト列全体を読み、行（セルのリスト）のリストを返す。
    /// ゲームの LoadCSV と同様、空行（0 セル）または終端で読み取りを終える。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadAll(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var cursor = new Cursor(data);
        var rows = new List<IReadOnlyList<string>>();
        while (true)
        {
            var row = cursor.ReadCsvRow();
            if (row is null || row.Count == 0)
            {
                break;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// 読み取り位置を保持するカーソル。可変状態は 1 回の解析の中だけに閉じ込める。
    /// </summary>
    private sealed class Cursor(byte[] buffer)
    {
        private int _offset;

        private bool CanRead => _offset < buffer.Length;

        /// <summary>
        /// ByteReader.ReadLine の移植。改行（LF/CR）までを UTF-8 で 1 行として返す。
        /// skipEmptyLines が true のとき、先頭の制御文字（改行を含む）を読み飛ばす。
        /// </summary>
        private string? ReadLine(bool skipEmptyLines)
        {
            var length = buffer.Length;
            if (skipEmptyLines)
            {
                while (_offset < length && buffer[_offset] < 32)
                {
                    _offset++;
                }
            }

            var end = _offset;
            if (end < length)
            {
                while (true)
                {
                    if (end < length)
                    {
                        var c = buffer[end++];
                        if (c == 10 || c == 13)
                        {
                            break;
                        }
                        continue;
                    }
                    // 終端に達した場合も「改行 1 文字分」を進めた扱いにする（元実装の挙動）
                    end++;
                    break;
                }
                var result = Encoding.UTF8.GetString(buffer, _offset, end - _offset - 1);
                _offset = end;
                return result;
            }

            _offset = length;
            return null;
        }

        /// <summary>
        /// ByteReader.ReadCSV の移植。1 レコード分のセルを返す（終端なら null）。
        /// 引用符内の改行は次の行と連結して読み進める。
        /// </summary>
        public List<string>? ReadCsvRow()
        {
            var cells = new List<string>();
            var text = string.Empty;
            var inQuotes = false;
            var start = 0;

            while (CanRead)
            {
                if (inQuotes)
                {
                    var continuation = ReadLine(skipEmptyLines: false);
                    if (continuation is null)
                    {
                        return null;
                    }
                    text = text + "\n" + continuation.Replace("\\n", "\n", StringComparison.Ordinal);
                }
                else
                {
                    var line = ReadLine(skipEmptyLines: true);
                    if (line is null)
                    {
                        return null;
                    }
                    text = line.Replace("\\n", "\n", StringComparison.Ordinal);
                    start = 0;
                }

                // 連結後は現在のフィールド先頭から走査し直す（元実装と同じ）
                for (int i = start, length = text.Length; i < length; i++)
                {
                    var ch = text[i];
                    if (ch == ',')
                    {
                        if (!inQuotes)
                        {
                            cells.Add(text[start..i]);
                            start = i + 1;
                        }
                    }
                    else if (ch == '"')
                    {
                        if (inQuotes)
                        {
                            if (i + 1 >= length)
                            {
                                cells.Add(text[start..i].Replace("\"\"", "\"", StringComparison.Ordinal));
                                return cells;
                            }
                            if (text[i + 1] != '"')
                            {
                                cells.Add(text[start..i].Replace("\"\"", "\"", StringComparison.Ordinal));
                                inQuotes = false;
                                if (text[i + 1] == ',')
                                {
                                    i++;
                                    start = i + 1;
                                }
                            }
                            else
                            {
                                // 連続する "" はエスケープされた引用符なので 1 文字読み飛ばす
                                i++;
                            }
                        }
                        else
                        {
                            start = i + 1;
                            inQuotes = true;
                        }
                    }
                }

                if (start < text.Length)
                {
                    if (inQuotes)
                    {
                        // 引用符が閉じていないので次の行と連結して続きを読む
                        continue;
                    }
                    cells.Add(text[start..]);
                }
                return cells;
            }

            return null;
        }
    }
}
