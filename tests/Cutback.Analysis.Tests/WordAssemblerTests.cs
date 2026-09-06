namespace Cutback.Analysis.Tests;

public sealed class WordAssemblerTests
{
    private static TokenTiming T(string text, double start, double end, float p = 0.9f) => new(text, start, end, p);

    [Fact]
    public void A_space_prefixed_token_starts_a_new_word()
    {
        var words = WordAssembler.FromTokens([T(" Hello", 0.0, 0.3), T(" world", 0.4, 0.8)]);

        words.Select(w => (w.Text, w.Start, w.End)).Should().Equal(("Hello", 0.0, 0.3), ("world", 0.4, 0.8));
    }

    [Fact]
    public void Tokens_without_a_leading_space_continue_the_current_word()
    {
        var words = WordAssembler.FromTokens([T(" un", 0.0, 0.1), T("believ", 0.1, 0.3), T("able", 0.3, 0.5)]);

        words.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Text = "unbelievable", Start = 0.0, End = 0.5 });
    }

    [Fact]
    public void Punctuation_only_tokens_attach_to_the_current_word_even_with_a_leading_space()
    {
        var words = WordAssembler.FromTokens([T(" Hello", 0.0, 0.3), T(",", 0.3, 0.3), T(" .", 0.3, 0.35), T(" world", 0.4, 0.8)]);

        words.Select(w => w.Text).Should().Equal("Hello,.", "world");
    }

    [Fact]
    public void Control_tokens_and_blank_tokens_are_skipped()
    {
        var words = WordAssembler.FromTokens([T("[_BEG_]", 0.0, 0.0), T(" um", 0.5, 0.7), T("[_TT_35]", 0.7, 0.7), T("", 0.7, 0.7), T("  ", 0.8, 0.8)]);

        words.Should().ContainSingle().Which.Text.Should().Be("um");
    }

    [Fact]
    public void The_first_token_starts_a_word_even_without_a_space()
    {
        var words = WordAssembler.FromTokens([T("Hi", 0.0, 0.2)]);

        words.Should().ContainSingle().Which.Text.Should().Be("Hi");
    }

    [Fact]
    public void Confidence_is_the_mean_token_probability()
    {
        var words = WordAssembler.FromTokens([T(" ab", 0.0, 0.1, 0.8f), T("cd", 0.1, 0.2, 0.6f)]);

        words[0].Confidence.Should().BeApproximately(0.7, 1e-6);
    }

    [Fact]
    public void A_word_with_no_duration_gets_ten_milliseconds()
    {
        var words = WordAssembler.FromTokens([T(" x", 1.0, 1.0)]);

        words[0].End.Should().BeApproximately(1.01, 1e-9);
    }

    [Fact]
    public void Empty_input_gives_no_words()
    {
        WordAssembler.FromTokens([]).Should().BeEmpty();
    }
}
