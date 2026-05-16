namespace Pointframe.Models;

public enum UpdateCheckInterval
{
    Never = 0,
    EveryDay = 1,
    EveryTwoDays = 2,
    EveryThreeDays = 3,
    EveryTwoHours = 4,
    EverySixHours = 5,
    EveryTwelveHours = 6,
#if DEBUG
    EveryThirtySeconds = 99,
#endif
}
