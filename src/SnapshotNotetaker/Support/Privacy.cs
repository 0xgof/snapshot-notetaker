using System.Text.RegularExpressions;

namespace SnapshotNotetaker.Support;

/// <summary>Masks personal details (Windows user name, profile path, computer name) in text that may be shared.</summary>
public static class Privacy
{
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
    private static readonly Regex? UserInPath = Environment.UserName.Length >= 2
        ? new Regex(@"(\\Users\\|/Users/|/home/)" + Regex.Escape(Environment.UserName) + @"(?=[\\/'"":]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        : null;
    private static readonly Regex? Machine = Environment.MachineName.Length >= 3
        ? new Regex(@"\b" + Regex.Escape(Environment.MachineName) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        : null;

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (Profile.Length > 3) text = text.Replace(Profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (UserInPath != null) text = UserInPath.Replace(text, "$1<user>");
        if (Machine != null) text = Machine.Replace(text, "<computer>");
        return text;
    }
}
