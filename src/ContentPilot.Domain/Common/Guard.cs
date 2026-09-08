using System.Runtime.CompilerServices;

namespace ContentPilot.Domain.Common;

/// <summary>
/// Small argument guards. Domain invariants are enforced at construction so an
/// invalid entity cannot exist, not merely fail validation later.
/// </summary>
public static class Guard
{
    public static string NotBlank(string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", name);
        }

        return value.Trim();
    }

    public static string MaxLength(string value, int max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value.Length > max)
        {
            throw new ArgumentException($"Value must be {max} characters or fewer (was {value.Length}).", name);
        }

        return value;
    }

    public static Guid NotEmpty(Guid value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", name);
        }

        return value;
    }

    public static int InRange(int value, int min, int max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Value must be between {min} and {max}.");
        }

        return value;
    }
}
