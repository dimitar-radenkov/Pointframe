namespace Pointframe.Services;

// Decides when the update card may appear on its own: only after something made an offer due (startup delay
// or a finished capture), and at most once per cooldown across restarts. Safety of the moment is the caller's
// job; an unsafe moment leaves the offer due.
public sealed class UpdateOfferPolicy
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(72);

    private bool _offerDue;

    public void MarkOfferDue() => _offerDue = true;

    public bool IsOfferDue(DateTime? lastOfferUtc, DateTime nowUtc) =>
        _offerDue && (lastOfferUtc is not { } lastOffer || nowUtc - lastOffer >= Cooldown || nowUtc < lastOffer);

    public void MarkOffered() => _offerDue = false;
}
