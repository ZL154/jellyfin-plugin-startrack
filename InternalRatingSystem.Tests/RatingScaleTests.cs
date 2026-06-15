using Jellyfin.Plugin.InternalRating.ExternalSync;
using Xunit;

public class RatingScaleTests
{
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(2.5, 5)]
    [InlineData(5.0, 10)]
    public void ToService10_DoublesStars(double stars, int expected)
        => Assert.Equal(expected, RatingScale.ToService10(stars));

    [Theory]
    [InlineData(1, 0.5)]
    [InlineData(5, 2.5)]
    [InlineData(10, 5.0)]
    public void FromService10_HalvesRating(int rating, double expected)
        => Assert.Equal(expected, RatingScale.FromService10(rating));

    [Fact]
    public void ToService10_Clamps() => Assert.Equal(10, RatingScale.ToService10(7.0));

    [Fact]
    public void FromService10_Clamps()
    {
        Assert.Equal(0.5, RatingScale.FromService10(0));
        Assert.Equal(5.0, RatingScale.FromService10(99));
    }
}
