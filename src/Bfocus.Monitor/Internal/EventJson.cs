using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Bfocus.Monitor.Internal;

/// <summary>JSON do evento escrito à mão (sem dependência). Nulo e coleção vazia não saem.</summary>
internal static class EventJson
{
    public static string Level(MonitorLevel level) => level switch
    {
        MonitorLevel.Fatal => "fatal",
        MonitorLevel.Warning => "warning",
        MonitorLevel.Info => "info",
        _ => "error",
    };

    public static string Serialize(MonitorEvent e)
    {
        var sb = new StringBuilder(1024);
        var w = new Obj(sb);
        w.Str("timestamp", e.Timestamp);
        w.Str("level", Level(e.Level));
        w.Str("release", e.Release);
        w.Str("environment", e.Environment);
        if (e.Exception != null)
        {
            w.Key("exception");
            var x = new Obj(sb);
            x.Str("type", e.Exception.Type);
            x.Str("message", e.Exception.Message ?? "");
            x.Key("frames");
            sb.Append('[');
            var first = true;
            foreach (var f in e.Exception.Frames ?? new List<MonitorFrame>())
            {
                if (f == null) continue;
                if (!first) sb.Append(',');
                first = false;
                var fo = new Obj(sb);
                fo.Str("file", f.File);
                fo.Str("function", f.Function);
                fo.Int("line", f.Line);
                fo.Int("col", f.Col);
                fo.Bool("inApp", f.InApp);
                fo.End();
            }
            sb.Append(']');
            x.End();
        }
        w.Str("transaction", e.Transaction);
        w.Str("url", e.Url);
        if (e.User != null && !string.IsNullOrEmpty(e.User.ExternalId))
        {
            w.Key("user");
            var u = new Obj(sb);
            u.Str("externalId", e.User.ExternalId);
            u.Str("userHash", e.User.UserHash);
            u.End();
        }
        if (e.Customer != null && !string.IsNullOrEmpty(e.Customer.ExternalId))
        {
            w.Key("customer");
            var c = new Obj(sb);
            c.Str("externalId", e.Customer.ExternalId);
            c.End();
        }
        if (e.Tags != null && e.Tags.Count > 0)
        {
            w.Key("tags");
            Map(sb, e.Tags);
        }
        if (e.Breadcrumbs != null && e.Breadcrumbs.Count > 0)
        {
            w.Key("breadcrumbs");
            sb.Append('[');
            var first = true;
            foreach (var b in e.Breadcrumbs)
            {
                if (b == null) continue;
                if (!first) sb.Append(',');
                first = false;
                var bo = new Obj(sb);
                bo.Str("timestamp", b.Timestamp);
                bo.Str("category", b.Category);
                bo.Str("message", b.Message);
                bo.Str("level", Level(b.Level));
                bo.End();
            }
            sb.Append(']');
        }
        if (e.Fingerprint != null && e.Fingerprint.Count > 0)
        {
            w.Key("fingerprint");
            sb.Append('[');
            var first = true;
            foreach (var p in e.Fingerprint)
            {
                if (p == null) continue;
                if (!first) sb.Append(',');
                first = false;
                Quote(sb, p);
            }
            sb.Append(']');
        }
        if (e.Contexts != null && e.Contexts.Count > 0)
        {
            w.Key("contexts");
            var co = new Obj(sb);
            foreach (var kv in e.Contexts)
            {
                if (kv.Key == null || kv.Value == null) continue;
                co.Key(kv.Key);
                Map(sb, kv.Value);
            }
            co.End();
        }
        if (e.Sdk != null)
        {
            w.Key("sdk");
            var s = new Obj(sb);
            s.Str("name", e.Sdk.Name);
            s.Str("version", e.Sdk.Version);
            s.End();
        }
        w.End();
        return sb.ToString();
    }

    private static void Map(StringBuilder sb, IDictionary<string, string> map)
    {
        var o = new Obj(sb);
        foreach (var kv in map) o.Str(kv.Key, kv.Value);
        o.End();
    }

    public static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20 || ch == (char)0x2028 || ch == (char)0x2029)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Escritor de objeto que só abre a vírgula quando o campo existe.</summary>
    private struct Obj
    {
        private readonly StringBuilder _sb;
        private bool _any;

        public Obj(StringBuilder sb)
        {
            _sb = sb;
            _any = false;
            sb.Append('{');
        }

        public void Key(string name)
        {
            if (_any) _sb.Append(',');
            _any = true;
            Quote(_sb, name);
            _sb.Append(':');
        }

        public void Str(string? name, string? value)
        {
            if (name == null || value == null) return;
            Key(name);
            Quote(_sb, value);
        }

        public void Int(string name, int? value)
        {
            if (value == null) return;
            Key(name);
            _sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
        }

        public void Bool(string name, bool value)
        {
            Key(name);
            _sb.Append(value ? "true" : "false");
        }

        public void End() => _sb.Append('}');
    }
}
