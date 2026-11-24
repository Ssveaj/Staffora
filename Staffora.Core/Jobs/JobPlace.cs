using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Portal;

namespace Staffora.Core.Jobs;

public readonly record struct JobPlace(string? Name, string? Street, string? PostalCode, string? City)
{
    public const string NoPlaceText = "No place given";

    public static JobPlace Of(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job.PlaceName, job.StreetAddress, job.PostalCode, job.City);
    }

    public static JobPlace Of(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new(order.PlaceName, order.StreetAddress, order.PostalCode, order.City);
    }

    public bool IsEmpty => Blank(Name) && Blank(Street) && Blank(PostalCode) && Blank(City);

    public string Text
    {
        get
        {
            var town = string.Join(" ", new[] { PostalCode, City }.Where(part => !Blank(part)).Select(part => part!.Trim()));
            return string.Join(", ", new[] { Name, Street, town }.Where(part => !Blank(part)).Select(part => part!.Trim()));
        }
    }

    public string? ShortText => !Blank(Name) ? Name!.Trim() : !Blank(City) ? City!.Trim() : null;

    public string TextOrNone => IsEmpty ? NoPlaceText : Text;

    public static string? ShortTextOf(string? name, string? city) => new JobPlace(name, null, null, city).ShortText;

    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);
}
