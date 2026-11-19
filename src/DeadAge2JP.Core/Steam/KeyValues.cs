using System.Text;

namespace DeadAge2JP.Core.Steam;

/// <summary>
/// Valve の KeyValues（VDF/ACF テキスト形式）の 1 ノード。
/// 値を持つ葉ノードか、子ノードを持つ枝ノードのどちらか。
/// </summary>
public sealed record KvNode(string Key, string? Value, IReadOnlyList<KvNode> Children)
{
    /// <summary>キーで子ノードを探す（Steam の慣習に合わせて大文字小文字は区別しない）。</summary>
    public KvNode? Child(string key) =>
        Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>子の葉ノードの値を取得する（無ければ null）。</summary>
    public string? ChildValue(string key) => Child(key)?.Value;
}

/// <summary>
/// KeyValues テキストの最小限のパーサ（libraryfolders.vdf / appmanifest_*.acf の読み取り用）。
/// 対応: 引用符付き文字列（\\ \" \n \t エスケープ）、引用符なしトークン、{ }、// コメント、[$WIN32] 等の条件式（無視）。
/// </summary>
public static class KeyValuesParser
{
    /// <summary>テキスト全体を解析し、最上位ノードを返す。</summary>
    public static KvNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Tokenize(text);
        var position = 0;
        var roots = ParseChildren(tokens, ref position, expectClosingBrace: false, depth: 0);
        return roots.Count switch
        {
            1 => roots[0],
            0 => throw new FormatException("KeyValues テキストにノードがありません。"),
            _ => new KvNode(string.Empty, null, roots),
        };
    }

    private enum TokenKind
    {
        String,
        Open,
        Close,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    /// <summary>
    /// 入れ子の深さの上限。Steam の libraryfolders.vdf・appmanifest は数段しかない。
    /// 上限が無いと、細工された（または壊れた）ファイルの数千段の入れ子で再帰がスタックオーバーフローになり、
    /// 例外として捕まえられないままパッチャーが起動時にメッセージも出さずに落ちる。
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>子ノードの並びを解析する（閉じ括弧か終端まで）。</summary>
    private static List<KvNode> ParseChildren(IReadOnlyList<Token> tokens, ref int position, bool expectClosingBrace, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException($"入れ子が深すぎます（{MaxDepth} 段まで）。");
        }
        var nodes = new List<KvNode>();
        while (position < tokens.Count)
        {
            var token = tokens[position];
            if (token.Kind == TokenKind.Close)
            {
                if (!expectClosingBrace)
                {
                    throw new FormatException("対応しない '}' があります。");
                }
                position++;
                return nodes;
            }
            if (token.Kind != TokenKind.String)
            {
                throw new FormatException("キーの位置に '{' があります。");
            }

            var key = token.Text;
            position++;
            if (position >= tokens.Count)
            {
                throw new FormatException($"キー '{key}' の値がありません。");
            }

            var next = tokens[position];
            if (next.Kind == TokenKind.Open)
            {
                position++;
                nodes.Add(new KvNode(key, null, ParseChildren(tokens, ref position, expectClosingBrace: true, depth + 1)));
            }
            else if (next.Kind == TokenKind.String)
            {
                position++;
                nodes.Add(new KvNode(key, next.Text, []));
            }
            else
            {
                throw new FormatException($"キー '{key}' の直後に '}}' があります。");
            }
        }

        if (expectClosingBrace)
        {
            throw new FormatException("'}' が不足しています。");
        }
        return nodes;
    }

    /// <summary>字句解析。条件式 [..] とコメントは捨てる。</summary>
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '{')
            {
                tokens.Add(new Token(TokenKind.Open, "{"));
                i++;
            }
            else if (c == '}')
            {
                tokens.Add(new Token(TokenKind.Close, "}"));
                i++;
            }
            else if (c == '[')
            {
                // [$WIN32] のような条件式は本ツールの用途では不要なので読み飛ばす
                while (i < text.Length && text[i] != ']')
                {
                    i++;
                }
                i++;
            }
            else if (c == '"')
            {
                i++;
                var builder = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        var escaped = text[i + 1];
                        builder.Append(escaped switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            _ => escaped,
                        });
                        i += 2;
                    }
                    else
                    {
                        builder.Append(text[i]);
                        i++;
                    }
                }
                if (i >= text.Length)
                {
                    throw new FormatException("引用符が閉じていません。");
                }
                i++;
                tokens.Add(new Token(TokenKind.String, builder.ToString()));
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
                {
                    i++;
                }
                tokens.Add(new Token(TokenKind.String, text[start..i]));
            }
        }
        return tokens;
    }
}
