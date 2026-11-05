using FullWorth.Backend.Data.Public.Status.V1;

namespace FullWorth.Backend.Service.Public.Status.V1;

public sealed class ReadFrameworkStatusV1
{
    public FrameworkStatusV1 Execute()
    {
        return new FrameworkStatusV1
        {
            Product = "FullWorth",
            Stage = "clean-start"
        };
    }
}
