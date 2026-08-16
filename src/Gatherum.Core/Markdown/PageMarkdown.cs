using System.Text;
using System.Text.Json.Nodes;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Gatherum.Core.Markdown;

/// <summary>Converts between the TipTap document JSON the editor produces and Markdown,
/// in both directions. This is the contract that lets MCP and the REST API speak
/// Markdown while the editor speaks ProseMirror.</summary>
public static class PageMarkdown
{
    public const string EmptyDoc = """{"type":"doc","content":[{"type":"paragraph"}]}""";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .Build();

    public static string ToDocJson(string markdown)
    {
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var content = new JsonArray(document.Select(ConvertBlock).Where(b => b is not null).ToArray());
        if (content.Count == 0)
            content.Add(Node("paragraph"));
        return new JsonObject { ["type"] = "doc", ["content"] = content }.ToJsonString();
    }

    public static string ToMarkdown(string docJson)
    {
        var doc = JsonNode.Parse(docJson) as JsonObject
            ?? throw new ArgumentException("Not a TipTap document.", nameof(docJson));
        var blocks = (doc["content"] as JsonArray ?? []).OfType<JsonObject>();
        return string.Join("\n\n", blocks.Select(WriteBlock).Where(s => s.Length > 0));
    }

    public static string ToPlainText(string docJson)
    {
        if (JsonNode.Parse(docJson) is not JsonObject doc)
            return "";
        var text = new StringBuilder();
        CollectText(doc, text);
        return text.ToString().Trim();
    }

    /// <summary>Node ids this document links to: @-mentions (node://id) and embedded
    /// file content (/api/files/id/content).</summary>
    public static IReadOnlySet<Guid> LinkedNodeIds(string docJson)
    {
        var ids = new HashSet<Guid>();
        if (JsonNode.Parse(docJson) is JsonObject doc)
            CollectLinks(doc, ids);
        return ids;
    }

    public static Guid? NodeIdFromUrl(string? url)
    {
        if (url is null)
            return null;
        if (url.StartsWith("node://", StringComparison.Ordinal) &&
            Guid.TryParse(url["node://".Length..], out var mentionId))
            return mentionId;
        var match = System.Text.RegularExpressions.Regex.Match(
            url, @"^/api/files/([0-9a-fA-F-]{36})/content");
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var fileId) ? fileId : null;
    }

    private static void CollectText(JsonObject node, StringBuilder text)
    {
        if (node["text"] is JsonValue value)
        {
            text.Append(value.GetValue<string>());
            return;
        }
        if ((string?)node["type"] == "mention")
        {
            text.Append('@').Append((string?)node["attrs"]?["label"]);
            return;
        }
        foreach (var child in (node["content"] as JsonArray ?? []).OfType<JsonObject>())
            CollectText(child, text);
        if ((string?)node["type"] is "paragraph" or "heading" or "codeBlock"
            or "listItem" or "taskItem" or "tableCell" or "tableHeader")
            text.Append('\n');
    }

    private static void CollectLinks(JsonObject node, HashSet<Guid> ids)
    {
        var type = (string?)node["type"];
        var url = type switch
        {
            "mention" => "node://" + (string?)node["attrs"]?["id"],
            "image" => (string?)node["attrs"]?["src"],
            _ => null,
        };
        if (NodeIdFromUrl(url) is { } id)
            ids.Add(id);
        foreach (var mark in (node["marks"] as JsonArray ?? []).OfType<JsonObject>())
            if ((string?)mark["type"] == "link" && NodeIdFromUrl((string?)mark["attrs"]?["href"]) is { } linkId)
                ids.Add(linkId);
        foreach (var child in (node["content"] as JsonArray ?? []).OfType<JsonObject>())
            CollectLinks(child, ids);
    }

    // ---- Markdown → TipTap ----

    private static JsonObject? ConvertBlock(Block block) => block switch
    {
        ParagraphBlock paragraph => Node("paragraph", ConvertInlines(paragraph.Inline)),
        HeadingBlock heading => Node("heading", ConvertInlines(heading.Inline),
            new JsonObject { ["level"] = heading.Level }),
        FencedCodeBlock fenced => CodeBlock(fenced.Info, fenced.Lines.ToString()),
        CodeBlock code => CodeBlock(null, code.Lines.ToString()),
        QuoteBlock quote => ConvertQuote(quote),
        ListBlock list => ConvertList(list),
        Table table => ConvertTable(table),
        ThematicBreakBlock => Node("horizontalRule"),
        _ => null,
    };

    private static JsonObject CodeBlock(string? language, string code)
    {
        var attrs = new JsonObject { ["language"] = string.IsNullOrWhiteSpace(language) ? null : language };
        var content = code.Length == 0 ? new JsonArray() : new JsonArray(Text(code));
        return Node("codeBlock", content, attrs);
    }

    private static JsonObject ConvertQuote(QuoteBlock quote)
    {
        var blocks = quote.Select(ConvertBlock).Where(b => b is not null).Select(b => b!).ToList();
        var marker = FirstTextOf(blocks.FirstOrDefault());
        var callout = marker is not null
            ? System.Text.RegularExpressions.Regex.Match(marker, @"^\[!(\w+)\]\s*")
            : System.Text.RegularExpressions.Match.Empty;
        if (!callout.Success)
            return Node("blockquote", new JsonArray(blocks.ToArray<JsonNode?>()));

        TrimLeadingText(blocks[0], callout.Value.Length);
        if (IsEmptyParagraph(blocks[0]))
            blocks.RemoveAt(0);
        if (blocks.Count == 0)
            blocks.Add(Node("paragraph"));
        return Node("callout", new JsonArray(blocks.ToArray<JsonNode?>()),
            new JsonObject { ["kind"] = callout.Groups[1].Value.ToLowerInvariant() });
    }

    /// <summary>Concatenated text of the leading text nodes — Markdig can split
    /// "[!tip] …" across several literals, so one node is not enough to see the marker.</summary>
    private static string? FirstTextOf(JsonObject? block)
    {
        var content = block?["content"] as JsonArray;
        if (content is null)
            return null;
        var text = new StringBuilder();
        foreach (var inline in content.OfType<JsonObject>())
        {
            if ((string?)inline["text"] is not { } chunk)
                break;
            text.Append(chunk);
        }
        return text.Length == 0 ? null : text.ToString();
    }

    private static void TrimLeadingText(JsonObject block, int length)
    {
        var content = block["content"] as JsonArray;
        while (length > 0 && content?.OfType<JsonObject>().FirstOrDefault() is { } first &&
            (string?)first["text"] is { } text)
        {
            var take = Math.Min(length, text.Length);
            length -= take;
            if (take == text.Length)
                content.RemoveAt(0);
            else
                first["text"] = text[take..];
        }
        if (content?.OfType<JsonObject>().FirstOrDefault() is { } next &&
            (string?)next["type"] == "hardBreak")
            content.RemoveAt(0);
    }

    private static bool IsEmptyParagraph(JsonObject block) =>
        (string?)block["type"] == "paragraph" && (block["content"] as JsonArray ?? []).Count == 0;

    private static JsonObject ConvertList(ListBlock list)
    {
        var items = list.OfType<ListItemBlock>().ToList();
        var isTaskList = items.Any(item => ItemTaskState(item) is not null);

        if (isTaskList)
        {
            var taskItems = items.Select(item => Node("taskItem",
                ConvertItemBlocks(item, stripTaskMarker: true),
                new JsonObject { ["checked"] = ItemTaskState(item) ?? false }));
            return Node("taskList", new JsonArray(taskItems.ToArray<JsonNode?>()));
        }

        var listItems = items.Select(item => Node("listItem", ConvertItemBlocks(item, false)));
        return list.IsOrdered
            ? Node("orderedList", new JsonArray(listItems.ToArray<JsonNode?>()),
                new JsonObject { ["start"] = int.TryParse(list.OrderedStart, out var start) ? start : 1 })
            : Node("bulletList", new JsonArray(listItems.ToArray<JsonNode?>()));
    }

    private static bool? ItemTaskState(ListItemBlock item) =>
        (item.FirstOrDefault() as ParagraphBlock)?.Inline?.FirstChild is TaskList task
            ? task.Checked
            : null;

    private static JsonArray ConvertItemBlocks(ListItemBlock item, bool stripTaskMarker)
    {
        var blocks = new JsonArray();
        foreach (var child in item)
        {
            if (ConvertBlock(child) is not { } converted)
                continue;
            if (stripTaskMarker && blocks.Count == 0 && child is ParagraphBlock)
                RemoveTaskMarker(converted);
            blocks.Add(converted);
        }
        if (blocks.Count == 0)
            blocks.Add(Node("paragraph"));
        return blocks;
    }

    private static void RemoveTaskMarker(JsonObject paragraph)
    {
        // Markdig surfaces "[x] " as a TaskList inline we skip during conversion, but the
        // space after it survives in the first literal.
        var content = paragraph["content"] as JsonArray;
        if (content?.OfType<JsonObject>().FirstOrDefault() is { } first &&
            (string?)first["text"] is { } text && text.StartsWith(' '))
        {
            if (text.Length == 1)
                content.RemoveAt(0);
            else
                first["text"] = text[1..];
        }
    }

    private static JsonObject ConvertTable(Table table)
    {
        var rows = new JsonArray();
        foreach (var row in table.OfType<TableRow>())
        {
            var cells = new JsonArray();
            foreach (var cell in row.OfType<TableCell>())
            {
                var cellBlocks = cell.Select(ConvertBlock).Where(b => b is not null).ToArray();
                cells.Add(Node(row.IsHeader ? "tableHeader" : "tableCell",
                    new JsonArray(cellBlocks.Length == 0 ? [Node("paragraph")] : cellBlocks)));
            }
            rows.Add(Node("tableRow", cells));
        }
        return Node("table", rows);
    }

    private static JsonArray ConvertInlines(ContainerInline? container)
    {
        var result = new JsonArray();
        if (container is null)
            return result;
        foreach (var inline in container)
            foreach (var node in ConvertInline(inline, []))
                result.Add(node);
        return result;
    }

    private static IEnumerable<JsonObject> ConvertInline(Inline inline, List<JsonObject> marks) =>
        inline switch
        {
            LiteralInline literal => [Text(literal.Content.ToString(), marks)],
            CodeInline code => [Text(code.Content, [.. marks, Mark("code")])],
            LineBreakInline { IsHard: true } => [Node("hardBreak")],
            LineBreakInline => [Text(" ", marks)],
            TaskList => [],
            EmphasisInline emphasis => ConvertChildren(emphasis, [.. marks, EmphasisMark(emphasis)]),
            LinkInline { IsImage: true } image => [ImageNode(image)],
            LinkInline link when IsMention(link) => [MentionNode(link)],
            LinkInline link => ConvertChildren(link,
                [.. marks, Mark("link", new JsonObject { ["href"] = link.Url })]),
            ContainerInline container => ConvertChildren(container, marks),
            _ => [],
        };

    private static IEnumerable<JsonObject> ConvertChildren(ContainerInline container, List<JsonObject> marks)
    {
        foreach (var child in container)
            foreach (var node in ConvertInline(child, marks))
                yield return node;
    }

    private static JsonObject EmphasisMark(EmphasisInline emphasis) => emphasis.DelimiterChar switch
    {
        '~' => Mark("strike"),
        _ => emphasis.DelimiterCount >= 2 ? Mark("bold") : Mark("italic"),
    };

    private static bool IsMention(LinkInline link) =>
        link.Url?.StartsWith("node://", StringComparison.Ordinal) == true &&
        InlineText(link).StartsWith('@');

    private static JsonObject MentionNode(LinkInline link) => Node("mention", content: null,
        new JsonObject
        {
            ["id"] = link.Url!["node://".Length..],
            ["label"] = InlineText(link)[1..],
        });

    private static JsonObject ImageNode(LinkInline image) => Node("image", content: null,
        new JsonObject { ["src"] = image.Url, ["alt"] = InlineText(image) });

    private static string InlineText(ContainerInline container) =>
        string.Concat(container.Select(i => i switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            ContainerInline inner => InlineText(inner),
            _ => "",
        }));

    private static JsonObject Node(string type, JsonArray? content = null, JsonObject? attrs = null)
    {
        var node = new JsonObject { ["type"] = type };
        if (attrs is not null)
            node["attrs"] = attrs;
        if (content is not null)
            node["content"] = content;
        return node;
    }

    private static JsonObject Text(string text, List<JsonObject>? marks = null)
    {
        var node = new JsonObject { ["type"] = "text", ["text"] = text };
        if (marks is { Count: > 0 })
            node["marks"] = new JsonArray(marks.Select(m => m.DeepClone()).ToArray());
        return node;
    }

    private static JsonObject Mark(string type, JsonObject? attrs = null)
    {
        var mark = new JsonObject { ["type"] = type };
        if (attrs is not null)
            mark["attrs"] = attrs;
        return mark;
    }

    // ---- TipTap → Markdown ----

    private static string WriteBlock(JsonObject block) => (string?)block["type"] switch
    {
        "paragraph" => WriteInlines(block),
        "heading" => new string('#', (int?)block["attrs"]?["level"] ?? 1) + " " + WriteInlines(block),
        "codeBlock" => WriteCodeBlock(block),
        "blockquote" => Quote(WriteChildren(block)),
        "callout" => Quote($"[!{(string?)block["attrs"]?["kind"] ?? "info"}]\n" + WriteChildren(block)),
        "bulletList" => WriteList(block, _ => "- "),
        "orderedList" => WriteList(block, i => $"{((int?)block["attrs"]?["start"] ?? 1) + i}. "),
        "taskList" => WriteTaskList(block),
        "table" => WriteTable(block),
        "horizontalRule" => "---",
        "image" => WriteImage(block),
        _ => WriteChildren(block),
    };

    private static string WriteChildren(JsonObject block) =>
        string.Join("\n\n", (block["content"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(WriteBlock).Where(s => s.Length > 0));

    private static string WriteCodeBlock(JsonObject block)
    {
        var language = (string?)block["attrs"]?["language"] ?? "";
        var code = string.Concat((block["content"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(t => (string?)t["text"]));
        return $"```{language}\n{code.TrimEnd('\n')}\n```";
    }

    private static string Quote(string content) =>
        string.Join("\n", content.Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line));

    private static string WriteList(JsonObject list, Func<int, string> marker)
    {
        var items = (list["content"] as JsonArray ?? []).OfType<JsonObject>();
        return string.Join("\n", items.Select((item, i) => WriteListItem(item, marker(i))));
    }

    private static string WriteTaskList(JsonObject list)
    {
        var items = (list["content"] as JsonArray ?? []).OfType<JsonObject>();
        return string.Join("\n", items.Select(item =>
            WriteListItem(item, (bool?)item["attrs"]?["checked"] == true ? "- [x] " : "- [ ] ")));
    }

    private static string WriteListItem(JsonObject item, string marker)
    {
        var blocks = (item["content"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var body = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
                body.Append(IsList(blocks[i]) ? "\n" : "\n\n");
            body.Append(WriteBlock(blocks[i]));
        }
        var indent = new string(' ', marker.Length);
        var lines = body.ToString().Split('\n');
        return marker + string.Join("\n",
            lines.Select((line, i) => i == 0 ? line : line.Length == 0 ? "" : indent + line));

        static bool IsList(JsonObject block) =>
            (string?)block["type"] is "bulletList" or "orderedList" or "taskList";
    }

    private static string WriteTable(JsonObject table)
    {
        var rows = (table["content"] as JsonArray ?? []).OfType<JsonObject>().Select(row =>
            (row["content"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(cell => WriteChildren(cell).Replace("\n", " ").Replace("|", "\\|"))
                .ToList())
            .ToList();
        if (rows.Count == 0)
            return "";
        var width = rows.Max(r => r.Count);
        var lines = new List<string> { Row(rows[0]), Row(Enumerable.Repeat("---", width).ToList()) };
        lines.AddRange(rows.Skip(1).Select(Row));
        return string.Join("\n", lines);

        string Row(List<string> cells) => "| " + string.Join(" | ", cells) + " |";
    }

    private static string WriteImage(JsonObject image) =>
        $"![{(string?)image["attrs"]?["alt"] ?? ""}]({(string?)image["attrs"]?["src"] ?? ""})";

    private static string WriteInlines(JsonObject block)
    {
        var text = new StringBuilder();
        foreach (var inline in (block["content"] as JsonArray ?? []).OfType<JsonObject>())
            text.Append(WriteInline(inline));
        return text.ToString();
    }

    private static string WriteInline(JsonObject inline) => (string?)inline["type"] switch
    {
        "text" => WrapMarks((string?)inline["text"] ?? "", inline["marks"] as JsonArray),
        "hardBreak" => "\\\n",
        "mention" =>
            $"[@{(string?)inline["attrs"]?["label"] ?? ""}](node://{(string?)inline["attrs"]?["id"] ?? ""})",
        "image" => WriteImage(inline),
        _ => "",
    };

    private static string WrapMarks(string text, JsonArray? marks)
    {
        if (marks is null)
            return Escape(text);
        var types = marks.OfType<JsonObject>().ToList();
        var isCode = types.Any(m => (string?)m["type"] == "code");
        var result = isCode ? $"`{text}`" : Escape(text);
        foreach (var mark in types)
        {
            result = (string?)mark["type"] switch
            {
                "bold" => $"**{result}**",
                "italic" => $"*{result}*",
                "strike" => $"~~{result}~~",
                "link" => $"[{result}]({(string?)mark["attrs"]?["href"] ?? ""})",
                _ => result,
            };
        }
        return result;
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '~' or '|')
                escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }
}
