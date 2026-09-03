namespace FullWorth.Web.Modules.Recovery;

public sealed class RecoveryOptions
{
    public const int DefaultCodeCount = 10;

    public int CodeCount { get; set; } = DefaultCodeCount;
}
