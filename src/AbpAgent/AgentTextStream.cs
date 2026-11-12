using System.Text;
using System.Text.RegularExpressions;

namespace AbpAgent;

/// <summary>
/// An agent's text as it's written, given on a Markdown block at a time, each block cleaned (<see cref="AgentText.Clean"/>)
/// before it's given, so a link or image never reaches a reader, not even for a moment. A block is what Markdown can't
/// read across: a paragraph (up to a blank line), a heading, a table row, a list item (up to the next item), or a fenced
/// code block (up to its closing fence); and a plain paragraph a sentence at a time, once it has some length, so a
/// one-paragraph reply shows as it's written too. Each piece is given only if what's been given, with it, still cleans to itself, so
/// no image or link can be made across two of them (a list item that was really a paragraph's next line, say): one that
/// would is held back and given with the next, and at the end, escaped whole. A reply's final text is cleaned whole as
/// the run keeps it, and replaces what was streamed.
/// </summary>
public sealed partial class AgentTextStream
{
    private readonly StringBuilder _block = new();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _given = new();
    private string? _fence;

    /// <summary>The start of the line being written was given (a sentence): the rest is the same paragraph's.</summary>
    private bool _midLine;

    /// <summary>The block so far is a plain paragraph, which may be given a sentence at a time.</summary>
    private bool _plain;

    /// <summary>The least a paragraph so far holds before a sentence of it is given on its own.</summary>
    private const int SentenceAtLeast = 40;

    /// <summary>Text the model wrote; the blocks it completed, cleaned, each with the newlines after it.</summary>
    public IReadOnlyList<string> Append(string text)
    {
        List<string> done = [];
        foreach (char c in text)
        {
            _line.Append(c);
            if (c == '\n')
            {
                if (_midLine)
                {
                    _block.Append(_line);
                    (_midLine, _plain) = (false, true);
                }
                else
                {
                    Line(_line.ToString(), done);
                }

                _line.Clear();
            }
            else if (c == ' ' && SentenceEnded())
            {
                GiveSoFar(done);
            }
        }

        return done;
    }

    /// <summary>The rest, at the end of the text: what's left, cleaned; empty when nothing is.</summary>
    public string Flush()
    {
        _block.Append(_line);
        _line.Clear();
        _fence = null;
        _midLine = false;
        if (_block.Length == 0)
        {
            return "";
        }

        string piece = Safe(AgentText.Clean(_block.ToString())) ?? AgentText.Escape(_block.ToString());
        _block.Clear();
        _given.Append(piece);
        return piece;
    }

    /// <summary>A plain paragraph's sentence just ended (its mark and a space), and the paragraph so far is long enough to give.</summary>
    private bool SentenceEnded()
    {
        if (_fence is not null || _line.Length < 2 || ".!?\u061F\u3002".IndexOf(_line[^2]) < 0 || _block.Length + _line.Length < SentenceAtLeast)
        {
            return false;
        }

        if (_midLine)
        {
            return true;
        }

        string line = _line.ToString();
        return _block.Length > 0 ? _plain : !Marked(line);
    }

    /// <summary>The paragraph so far, line and all, given cleaned when that's safe; the rest of its line comes after.</summary>
    private void GiveSoFar(List<string> done)
    {
        if (Safe(AgentText.Clean(_block.ToString() + _line)) is not { } piece)
        {
            return;
        }

        _block.Clear();
        _line.Clear();
        _given.Append(piece);
        done.Add(piece);
        _midLine = true;
    }

    /// <summary>A line that starts something other than a plain paragraph: a fence, a heading, a table row, a list item, a quote.</summary>
    private static bool Marked(string line) => FenceOf(line) is not null || OneLine().IsMatch(line) || ListItem().IsMatch(line) || line.TrimStart().StartsWith('>');

    /// <summary>The piece, when what's been given with it still cleans to itself; else null.</summary>
    private string? Safe(string piece)
    {
        string joined = _given + piece;
        return AgentText.Clean(joined) == joined ? piece : null;
    }

    private void Line(string line, List<string> done)
    {
        if (_fence is not null)
        {
            _block.Append(line);
            if (FenceOf(line) is { } closing && closing[0] == _fence[0] && closing.Length >= _fence.Length && line.Trim().Trim(_fence[0]).Length == 0)
            {
                _fence = null;
                Give(done);
            }

            return;
        }

        if (FenceOf(line) is { } opening)
        {
            Give(done);
            _fence = opening;
            _block.Append(line);
            return;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            _block.Append(line);
            Give(done);
            return;
        }

        if (OneLine().IsMatch(line))
        {
            // A heading or a table row stands alone: what came before is done, and so is it.
            Give(done);
            _block.Append(line);
            Give(done);
            return;
        }

        if (ListItem().IsMatch(line))
        {
            Give(done); // the item before it, if any, is done
        }

        if (_block.Length == 0)
        {
            _plain = !Marked(line);
        }

        _block.Append(line);
    }

    /// <summary>The block so far is done: given cleaned, unless with what's been given it would make something; then it waits for more.</summary>
    private void Give(List<string> done)
    {
        if (_block.Length == 0 || Safe(AgentText.Clean(_block.ToString())) is not { } piece)
        {
            return;
        }

        _block.Clear();
        _given.Append(piece);
        done.Add(piece);
    }

    /// <summary>A fence's opening marks (<c>```</c> or <c>~~~</c>, three or more), when the line opens or closes one.</summary>
    private static string? FenceOf(string line) => Fence().Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Fence();

    [GeneratedRegex(@"^ {0,3}(#{1,6}(\s|$)|\|)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OneLine();

    [GeneratedRegex(@"^ {0,3}([-*+]|\d{1,9}[.)])\s", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListItem();
}
