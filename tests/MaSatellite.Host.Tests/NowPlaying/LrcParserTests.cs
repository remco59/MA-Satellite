using MaSatellite.Host.NowPlaying;

namespace MaSatellite.Host.Tests.NowPlaying;

public class LrcParserTests
{
    private const string Sample = "[ar:Someone]\n[ti:Song]\n[00:12.50]First line\n[00:20.00]Second line\n[01:05.5][02:00.00]Chorus\n[offset:+500]\n";

    [Fact]
    public void Parses_lines_orders_them_and_applies_offset()
    {
        var lines = LrcParser.Parse(Sample);
        Assert.Equal(4, lines.Count);
        Assert.Equal("First line", lines[0].Text);
        Assert.Equal(12.0, lines[0].StartSeconds, 3);  // +500 ms offset: lyrics appear 0.5 s sooner
        Assert.Equal(new[] { "First line", "Second line", "Chorus", "Chorus" }, lines.Select(l => l.Text));
        Assert.Equal(65.0, lines[2].StartSeconds, 3);
        Assert.Equal(119.5, lines[3].StartSeconds, 3);
    }

    [Fact]
    public void Finds_the_current_line_for_a_position()
    {
        var lines = LrcParser.Parse(Sample);
        Assert.Equal(-1, LrcParser.IndexAt(lines, 5));
        Assert.Equal(0, LrcParser.IndexAt(lines, 12.0));
        Assert.Equal(1, LrcParser.IndexAt(lines, 30));
        Assert.Equal(3, LrcParser.IndexAt(lines, 999));
        Assert.Equal(-1, LrcParser.IndexAt([], 10));
    }

    [Fact]
    public void Plain_text_is_not_synced_and_garbage_parses_to_nothing()
    {
        Assert.False(LrcParser.LooksSynced("Just some words\nOn two lines"));
        Assert.False(LrcParser.LooksSynced("[00:01.00]only one stamp"));
        Assert.True(LrcParser.LooksSynced("[00:01.00]a\n[00:02.00]b"));
        Assert.Empty(LrcParser.Parse("no stamps here"));
        Assert.Empty(LrcParser.Parse(null));
    }

    [Fact]
    public void Plain_text_conversion_strips_stamps_and_offset()
    {
        Assert.Equal("a\nb", LrcParser.ToPlainText("[00:01.00]a\n[offset:100]\n[00:02.00]b").Replace("\n\n", "\n").Trim());
    }
}
