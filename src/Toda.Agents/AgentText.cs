using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Toda.Agents;

/// <summary>
/// What agents write into rich text is cleaned as it's written (ADR 0086, 0091), so nothing they write can carry data
/// out by being displayed or mailed. The text is parsed as Markdown (Markdig), not matched by patterns, so every form the
/// grammar takes is seen:
/// <list type="bullet">
/// <item>an image from anywhere but the item's own files (<c>attachment:</c>) becomes its text and address;</item>
/// <item>a link shows its address beside its text, mentions (<c>account:</c>, <c>group:</c>) and files kept;</item>
/// <item>a reference definition becomes its label and address, as text;</item>
/// <item>raw HTML, inline or a block, is escaped: agents have no need of it;</item>
/// <item>a code span or a kept link with a "|" in it becomes text, since the browser cuts table rows there first.</item>
/// </list>
/// The rest of the text stays as it was written. A host's context calls <see cref="CleanWrites"/> as it saves, with the
/// rich text properties of its entities.
/// </summary>
public static class AgentText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().UsePipeTables().UseTaskLists().Build();

    /// <summary>Cleans one Markdown text. Cleaning is repeated until nothing is left to clean; text it can't settle is escaped whole.</summary>
    public static string Clean(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown ?? "";
        }

        string text = markdown;
        for (int pass = 0; pass < 3; pass++)
        {
            string cleaned = Once(text);
            if (cleaned == text)
            {
                return text;
            }

            text = cleaned;
        }

        return Once(text) == text ? text : Escape(markdown);
    }

    /// <summary>Cleans, in a save while an agent's run acts (<see cref="AgentActs.Now"/>), the named text properties of the entities of these types.</summary>
    public static void CleanWrites(ChangeTracker changes, IReadOnlyDictionary<Type, string[]> properties)
    {
        if (AgentActs.Now is not null)
        {
            CleanEntries(changes, properties);
        }
    }

    /// <summary>Cleans the named text properties of the entities of these types that a save writes, whoever acts: for a host that knows an agent acts its own way.</summary>
    public static void CleanEntries(ChangeTracker changes, IReadOnlyDictionary<Type, string[]> properties)
    {
        foreach (EntityEntry entry in changes.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            if (!properties.TryGetValue(entry.Entity.GetType(), out string[]? names))
            {
                continue;
            }

            foreach (string name in names)
            {
                if (entry.Property(name) is { CurrentValue: string value } written)
                {
                    string cleaned = Clean(value);
                    if (cleaned != value)
                    {
                        written.CurrentValue = cleaned;
                    }
                }
            }
        }
    }

    /// <summary>One pass: each image, link, reference definition and piece of HTML the parser finds, replaced in the text where it was.</summary>
    private static string Once(string text)
    {
        MarkdownDocument document = Markdown.Parse(text, Pipeline);
        List<(int Start, int End, string With)> edits = [];
        foreach (MarkdownObject node in document.Descendants())
        {
            if (node.Span.Start < 0 || node.Span.End >= text.Length || node.Span.End < node.Span.Start)
            {
                continue;
            }

            // The browser reads tables as GitHub does, cutting a row at every "|" before code or links are read, so a code
            // span or a kept link that holds one isn't kept as it is: a cut through it could make an image of its rest.
            string source = text[node.Span.Start..(node.Span.End + 1)];
            bool piped = source.Contains('|');
            string? with = node switch
            {
                LinkInline { IsImage: true } image when !Kept(image.Url) || piped => $"{Escape(Plain(image))} ({Escape(image.Url ?? image.Reference?.Url ?? "")})",
                LinkInline { IsImage: false } link when !Kept(link.Url) || piped => $"{Escape(Plain(link))} ({Escape(link.Url ?? link.Reference?.Url ?? "")})",
                LinkReferenceDefinition definition when !Kept(definition.Url) || piped => $"{Escape(definition.Label ?? "")}: {Escape(definition.Url ?? "")}",
                CodeInline code when piped => Escape(code.Content),
                HtmlInline or HtmlBlock => Escape(source),
                _ => null,
            };
            if (with is not null)
            {
                edits.Add((node.Span.Start, node.Span.End, with));
            }
        }

        // Outermost only: a link around an image is replaced whole, its image with it.
        List<(int Start, int End, string With)> outermost = [];
        foreach ((int Start, int End, string With) edit in edits.OrderBy(e => e.Start).ThenByDescending(e => e.End))
        {
            if (outermost.Count == 0 || edit.Start > outermost[^1].End)
            {
                outermost.Add(edit);
            }
        }

        StringBuilder result = new(text);
        for (int i = outermost.Count - 1; i >= 0; i--)
        {
            (int start, int end, string with) = outermost[i];
            result.Remove(start, end - start + 1).Insert(start, with);
        }

        return result.ToString();
    }

    /// <summary>A link's or an image's words, as plain text: what its label says, without the marks.</summary>
    private static string Plain(ContainerInline container)
    {
        StringBuilder words = new();
        foreach (Inline inline in container.Descendants<Inline>())
        {
            switch (inline)
            {
                case LiteralInline literal:
                    words.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    words.Append(code.Content);
                    break;
                case HtmlInline html:
                    words.Append(html.Tag);
                    break;
                case LineBreakInline:
                    words.Append(' ');
                    break;
            }
        }

        return words.ToString();
    }

    /// <summary>Text that Markdown reads as written: its marks escaped, so it can't make a link, an image or HTML again.</summary>
    private static string Escape(string text)
    {
        StringBuilder escaped = new(text.Length + 8);
        foreach (char c in text)
        {
            if (c is '\\' or '[' or ']' or '(' or ')' or '!' or '<' or '>' or '`' or '*' or '_' or '|')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    private static bool Kept(string? url) =>
        url is not null && (url.StartsWith("attachment:", StringComparison.Ordinal) || url.StartsWith("account:", StringComparison.Ordinal)
            || url.StartsWith("group:", StringComparison.Ordinal));
}
