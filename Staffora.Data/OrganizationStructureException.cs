namespace Staffora.Data;

public sealed class OrganizationStructureException : InvalidOperationException
{
    public OrganizationStructureException(string message) : base(message)
    {
    }
}
