using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class UpdateOfferPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsOfferDue_NothingMadeAnOfferDue_ReturnsFalse()
    {
        var sut = new UpdateOfferPolicy();

        Assert.False(sut.IsOfferDue(null, Now));
    }

    [Fact]
    public void IsOfferDue_StaysDueUntilOffered()
    {
        var sut = new UpdateOfferPolicy();
        sut.MarkOfferDue();

        Assert.True(sut.IsOfferDue(null, Now));
        Assert.True(sut.IsOfferDue(null, Now));
        sut.MarkOffered();
        Assert.False(sut.IsOfferDue(null, Now));
    }

    [Fact]
    public void IsOfferDue_RespectsPersisted72HourCooldown()
    {
        var sut = new UpdateOfferPolicy();
        var lastOffer = Now.AddHours(-71);
        sut.MarkOfferDue();

        Assert.False(sut.IsOfferDue(lastOffer, Now));
        Assert.True(sut.IsOfferDue(lastOffer, lastOffer.AddHours(72)));
    }

    [Fact]
    public void IsOfferDue_LastOfferInTheFuture_TreatsTheClockChangeAsExpired()
    {
        var sut = new UpdateOfferPolicy();
        sut.MarkOfferDue();

        Assert.True(sut.IsOfferDue(Now.AddDays(5), Now));
    }
}
