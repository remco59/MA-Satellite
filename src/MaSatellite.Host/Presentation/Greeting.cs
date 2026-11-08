namespace MaSatellite.Host.Presentation;

public static class Greeting
{
    public static string PartOfDay(int hour) => hour switch
    {
        >= 5 and < 12 => "morning",
        >= 12 and < 18 => "afternoon",
        >= 18 and < 23 => "evening",
        _ => "night",
    };

    /// <summary>"Good afternoon, Remco" when a plain first name is known, otherwise just "Good afternoon".</summary>
    public static string For(DateTime now, string? userName)
    {
        var text = now.Hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 18 => "Good afternoon",
            >= 18 and < 23 => "Good evening",
            _ => "Good night",
        };

        return !string.IsNullOrWhiteSpace(userName) && userName.All(char.IsLetter)
            ? $"{text}, {char.ToUpperInvariant(userName[0])}{userName[1..].ToLowerInvariant()}"
            : text;
    }
}
