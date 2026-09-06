using Whisper.net.Ggml;

namespace Cutback.Analysis.Tests;

public sealed class WhisperModelInfoTests
{
    [Fact]
    public void Every_model_has_info_with_a_unique_key()
    {
        var infos = Enum.GetValues<WhisperModel>().Select(WhisperModelInfo.For).ToList();

        infos.Select(i => i.Key).Should().OnlyHaveUniqueItems();
        infos.Should().OnlyContain(i => i.ApproximateBytes > 0);
        WhisperModelInfo.All.Select(i => i.Model).Should().BeEquivalentTo(Enum.GetValues<WhisperModel>());
    }

    [Fact]
    public void Base_en_is_the_default()
    {
        WhisperModelInfo.Default.Model.Should().Be(WhisperModel.BaseEn);
        WhisperModelInfo.Default.Key.Should().Be("base.en");
        WhisperModelInfo.Default.GgmlType.Should().Be(GgmlType.BaseEn);
        WhisperModelInfo.Default.FileName.Should().Be("ggml-base.en.bin");
    }

    [Theory]
    [InlineData("tiny.en", WhisperModel.TinyEn)]
    [InlineData("small.en", WhisperModel.SmallEn)]
    [InlineData("MEDIUM.EN", WhisperModel.MediumEn)]
    [InlineData("large", WhisperModel.BaseEn)]
    [InlineData("", WhisperModel.BaseEn)]
    [InlineData(null, WhisperModel.BaseEn)]
    public void Parse_maps_keys_and_falls_back_to_the_default(string? key, WhisperModel expected)
    {
        WhisperModelInfo.Parse(key).Should().Be(expected);
    }

    [Fact]
    public void Display_name_shows_the_approximate_size_in_megabytes()
    {
        WhisperModelInfo.For(WhisperModel.BaseEn).DisplayName.Should().Be("base.en (~148 MB)");
    }
}
