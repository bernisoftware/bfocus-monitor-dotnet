using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Bfocus.Monitor.Internal;

/// <summary>Frame cru, na ordem do runtime (de DENTRO para fora).</summary>
internal sealed class RawFrame
{
    public string? Function { get; set; }
    public string? File { get; set; }
    public int Line { get; set; }
    public int Col { get; set; }
    /// <summary>Frame do próprio monitor (assembly Bfocus.Monitor*): nunca é do sistema.</summary>
    public bool Own { get; set; }
}

internal static class StackFrames
{
    public const int MaxFrames = 60;
    private static readonly string[] LibraryPrefixes = { "System.", "Microsoft.", "Windows.", "Internal.", "Interop." };
    private static readonly string[] OwnAssemblies = { "Bfocus.Monitor", "Bfocus.Monitor.AspNetCore" };

    /// <summary>Frames da exceção, já na ordem do contrato (de FORA para DENTRO).</summary>
    public static List<MonitorFrame> FromException(Exception ex, IList<string> inAppPrefixes)
    {
        var raw = new List<RawFrame>();
        try
        {
            var trace = new StackTrace(ex, true);
            foreach (var f in trace.GetFrames() ?? Array.Empty<StackFrame>())
            {
                if (f == null) continue;
                var method = f.GetMethod();
                var fn = FunctionName(method);
                var file = f.GetFileName();
                if (fn == null && file == null) continue;
                raw.Add(new RawFrame
                {
                    Function = fn, File = file, Line = f.GetFileLineNumber(), Col = f.GetFileColumnNumber(),
                    Own = IsOwn(method),
                });
            }
        }
        catch
        {
            // rastro ilegível: segue sem frames
        }
        return Convert(raw, inAppPrefixes);
    }

    /// <summary>De dentro para fora (como o .NET dá) → de fora para dentro, com <c>inApp</c>.</summary>
    public static List<MonitorFrame> Convert(IList<RawFrame> innerToOuter, IList<string> inAppPrefixes)
    {
        var cwd = CurrentDirectory();
        var outList = new List<MonitorFrame>(Math.Min(innerToOuter.Count, MaxFrames));
        // Corta pelos mais EXTERNOS: os mais internos (onde estourou) são os que importam.
        var take = Math.Min(innerToOuter.Count, MaxFrames);
        for (var i = take - 1; i >= 0; i--)
        {
            var r = innerToOuter[i];
            outList.Add(new MonitorFrame
            {
                File = Relative(r.File, cwd),
                Function = r.Function,
                Line = r.Line > 0 ? r.Line : null,
                Col = r.Col > 0 ? r.Col : null,
                InApp = !r.Own && IsInApp(r.Function, inAppPrefixes),
            });
        }
        return outList;
    }

    public static bool IsInApp(string? function, IList<string> inAppPrefixes)
    {
        if (string.IsNullOrEmpty(function)) return false;
        foreach (var p in inAppPrefixes)
            if (!string.IsNullOrEmpty(p) && function!.StartsWith(p, StringComparison.Ordinal)) return true;
        foreach (var p in LibraryPrefixes)
            if (function!.StartsWith(p, StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool IsOwn(MethodBase? method)
    {
        try
        {
            var name = method?.DeclaringType?.Assembly.GetName().Name;
            return name != null && Array.IndexOf(OwnAssemblies, name) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary><c>Namespace.Classe.Metodo</c>; método de async/iterador volta ao nome que o programador escreveu.</summary>
    internal static string? FunctionName(MethodBase? method)
    {
        if (method == null) return null;
        var type = method.DeclaringType;
        var name = method.Name;
        if (type == null) return name;
        // async/iterador: Acme.Pedido+<Calcular>d__5.MoveNext → Acme.Pedido.Calcular
        if (type.Name.StartsWith("<", StringComparison.Ordinal) && type.DeclaringType != null)
        {
            var close = type.Name.IndexOf('>');
            if (close > 1)
            {
                name = type.Name.Substring(1, close - 1);
                type = type.DeclaringType;
            }
        }
        var typeName = (type.FullName ?? type.Name).Replace('+', '.');
        var generic = typeName.IndexOf('[');
        if (generic > 0) typeName = typeName.Substring(0, generic);
        return typeName + "." + name;
    }

    private static string? CurrentDirectory()
    {
        try
        {
            var d = Directory.GetCurrentDirectory();
            if (string.IsNullOrEmpty(d) || d == "/" ) return null;
            return d.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? d : d + Path.DirectorySeparatorChar;
        }
        catch
        {
            return null;
        }
    }

    private static string? Relative(string? file, string? cwd)
    {
        if (file == null || cwd == null) return file;
        return file.StartsWith(cwd, StringComparison.Ordinal) && file.Length > cwd.Length ? file.Substring(cwd.Length) : file;
    }
}
