using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class FillerDetectorTests
{
    private static Word W(string text, double start) => new(text, start, start + 0.2, 0.9);

    [Theory]
    [InlineData("Um,", "um")]
    [InlineData("  UH...", "uh")]
    [InlineData("hmm", "hmm")]
    [InlineData("\"er\"", "er")]
    [InlineData("uh-huh", "uh-huh")]
    public void Normalize_lower_cases_and_strips_surrounding_punctuation(string input, string expected)
    {
        FillerDetector.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Default_words_are_the_documented_list()
    {
        FillerDetector.DefaultWords.Should().Equal("um", "umm", "uh", "uhh", "er", "erm", "ah", "hmm", "hm", "mm");
    }

    [Fact]
    public void Find_returns_matching_words_in_timeline_order()
    {
        var transcript = new[] { W("So", 0.0), W("um,", 0.5), W("this", 1.0), W("Uh", 1.5), W("works", 2.0) };

        var found = FillerDetector.Find(transcript, FillerDetector.DefaultWords);

        found.Select(w => w.Start).Should().Equal(0.5, 1.5);
    }

    [Fact]
    public void Find_matches_the_supplied_list_not_the_default()
    {
        var transcript = new[] { W("um", 0.0), W("like", 0.5) };

        var found = FillerDetector.Find(transcript, ["Like"]);

        found.Should().ContainSingle().Which.Text.Should().Be("like");
    }

    [Fact]
    public void Hyphenated_forms_do_not_match_their_prefix()
    {
        var transcript = new[] { W("uh-huh", 0.0) };

        FillerDetector.Find(transcript, ["uh"]).Should().BeEmpty();
    }

    [Fact]
    public void Blank_entries_in_the_list_are_ignored()
    {
        var transcript = new[] { W("", 0.0), W("um", 0.5) };

        var found = FillerDetector.Find(transcript, ["", "  ", "um"]);

        found.Should().ContainSingle().Which.Start.Should().Be(0.5);
    }
}
