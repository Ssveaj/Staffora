namespace Staffora.Data;

public sealed class CustomFieldException : InvalidOperationException
{
    public CustomFieldException(string message) : base(message)
    {
    }
}
