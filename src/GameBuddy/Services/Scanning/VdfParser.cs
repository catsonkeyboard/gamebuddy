namespace GameBuddy.Services.Scanning;

/// <summary>
/// 极简 VDF（Valve KeyValues 文本格式）解析器，够用即可：
/// 支持 "key" "value" 与 "key" { ... } 两种结构，忽略 // 注释。
/// </summary>
public static class VdfParser
{
    /// <summary>
    /// 解析结果为 { 根对象名: { ...内容... } }，例如 { "AppState": {...} } 或 { "libraryfolders": {...} }。
    /// </summary>
    public static Dictionary<string, object> Parse(string text)
    {
        var reader = new Reader(text);
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        reader.ReadRoot(root);
        return root;
    }

    public static string? GetString(Dictionary<string, object> node, string key)
        => node.TryGetValue(key, out var v) && v is string s ? s : null;

    public static Dictionary<string, object>? GetObject(Dictionary<string, object> node, string key)
        => node.TryGetValue(key, out var v) && v is Dictionary<string, object> child ? child : null;

    private sealed class Reader
    {
        private readonly string _s;
        private int _i;

        public Reader(string s) => _s = s;

        /// <summary>读取最外层命名对象。</summary>
        public void ReadRoot(Dictionary<string, object> root)
        {
            SkipWhitespace();
            while (_i < _s.Length && _s[_i] != '}' && _s[_i] != '{')
            {
                var key = ReadToken();
                SkipWhitespace();

                if (_i < _s.Length && _s[_i] == '{')
                {
                    var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    ReadObject(child);
                    root[key ?? "root"] = child;
                    return;
                }

                var value = ReadToken();
                if (key is not null && value is not null) root[key] = value;
                SkipWhitespace();
            }

            // 没有命名外层（直接以 { 开头）时，兜底解析为匿名内容
            if (_i < _s.Length && _s[_i] == '{')
            {
                var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                ReadObject(child);
                root["root"] = child;
            }
        }

        public void ReadObject(Dictionary<string, object> target)
        {
            SkipWhitespace();
            if (_i >= _s.Length) return;
            if (_s[_i] != '{') return;
            _i++; // consume '{'

            while (_i < _s.Length)
            {
                SkipWhitespace();
                if (_i >= _s.Length) return;
                if (_s[_i] == '}')
                {
                    _i++;
                    return;
                }

                var key = ReadToken();
                if (key is null) return;
                SkipWhitespace();

                if (_i < _s.Length && _s[_i] == '{')
                {
                    var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    ReadObject(child);
                    target[key] = child;
                }
                else
                {
                    var value = ReadToken();
                    if (value is not null) target[key] = value;
                }
            }
        }

        private void SkipWhitespace()
        {
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (char.IsWhiteSpace(c))
                {
                    _i++;
                }
                else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                {
                    while (_i < _s.Length && _s[_i] != '\n') _i++;
                }
                else
                {
                    break;
                }
            }
        }

        private string? ReadToken()
        {
            if (_i >= _s.Length) return null;

            if (_s[_i] == '"')
            {
                _i++;
                var sb = new System.Text.StringBuilder();
                while (_i < _s.Length)
                {
                    var c = _s[_i];
                    if (c == '\\' && _i + 1 < _s.Length)
                    {
                        sb.Append(_s[_i + 1]);
                        _i += 2;
                        continue;
                    }

                    if (c == '"')
                    {
                        _i++;
                        break;
                    }

                    sb.Append(c);
                    _i++;
                }

                return sb.ToString();
            }

            if (_s[_i] == '{' || _s[_i] == '}') return null;

            var start = _i;
            while (_i < _s.Length && !char.IsWhiteSpace(_s[_i]) && _s[_i] != '{' && _s[_i] != '}') _i++;
            var token = _s[start.._i];
            return token.Length == 0 ? null : token;
        }
    }
}
