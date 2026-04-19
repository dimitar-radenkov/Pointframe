namespace Pointframe.Models;

public enum UpdateCheckInterval
{
    Never = 0,
    EveryDay = 1,
    EveryTwoDays = 2,
    EveryThreeDays = 3,
#if DEBUG
    EveryThirtySeconds = 99,
#endif
}
