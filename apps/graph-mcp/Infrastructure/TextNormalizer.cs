using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace GraphMcp.Infrastructure;

public static class TextNormalizer
{
    public static async Task<(string Text, bool Truncated)> NormalizeAsync(string? value, bool html, int maxChars, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!html) return Normalize(value, false, maxChars, cancellationToken);
        var document = await new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocumentAsync(value ?? "", cancellationToken);
        var text = new StringBuilder();
        Visit(document.Body ?? document.DocumentElement, text, cancellationToken);
        return Normalize(text.ToString(), false, maxChars, cancellationToken);
    }

    public static (string Text, bool Truncated) Normalize(string? value, bool html, int maxChars, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = value ?? "";
        if (html)
        {
            var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(source);
            var text = new StringBuilder();
            Visit(document.Body ?? document.DocumentElement, text, cancellationToken);
            source = text.ToString();
        }
        var output = new StringBuilder(Math.Min(source.Length, maxChars));
        var truncated = false;
        var previousCarriageReturn = false;
        var processed = 0;
        foreach (var rune in source.EnumerateRunes())
        {
            if (++processed % 1024 == 0) cancellationToken.ThrowIfCancellationRequested();
            if (rune.Value == '\n' && previousCarriageReturn) { previousCarriageReturn = false; continue; }
            previousCarriageReturn = rune.Value == '\r';
            var normalized = rune.Value == '\r' ? "\n" : rune.ToString();
            if (Rune.IsControl(rune) && rune.Value is not ('\r' or '\n' or '\t')) continue;
            if (output.Length + normalized.Length > maxChars) { truncated = true; break; }
            output.Append(normalized);
        }
        return (output.ToString().Trim(), truncated);
    }

    private static void Visit(INode? node, StringBuilder result, CancellationToken cancellationToken)
    {
        if (node is null) return;
        // Mail HTML is untrusted. An explicit stack avoids process stack exhaustion
        // when a message contains deeply nested elements.
        var pending = new Stack<(INode Node, bool Closing)>();
        var visited = 0;
        pending.Push((node, false));
        while (pending.TryPop(out var current))
        {
            if (++visited % 1024 == 0) cancellationToken.ThrowIfCancellationRequested();
            if (current.Node is IElement unsafeElement && unsafeElement.LocalName is "script" or "style" or "template" or "noscript" or "iframe" or "object" or "embed" or "svg" or "head") continue;
            if (current.Node.NodeType == NodeType.Text) { result.Append(current.Node.TextContent); continue; }
            var block = current.Node is IElement element && element.LocalName is "p" or "div" or "br" or "li" or "tr" or "h1" or "h2" or "h3" or "blockquote" or "section";
            if (block && result.Length > 0 && result[^1] != '\n') result.Append('\n');
            if (current.Closing) continue;
            if (block) pending.Push((current.Node, true));
            for (var i = current.Node.ChildNodes.Length - 1; i >= 0; i--) pending.Push((current.Node.ChildNodes[i], false));
        }
    }
}
