using System.Linq;

namespace FleetDesktop;

/// <summary>
/// Detects Fleet's device-page error screen from the rendered DOM.
///
/// Fleet answers with HTTP 200 and error copy when a device token has expired, so there
/// is no status code to key off. Requiring more than one phrase to match is what keeps
/// legitimate page content from tripping it.
///
/// Shared by <see cref="MainWindow"/> and <see cref="ToastWindow"/>: with a copy in each,
/// a reword of Fleet's error copy would silently break detection in one and not the other.
/// Mirrors FleetErrorPage.swift in the macOS app.
/// </summary>
internal static class FleetErrorPage
{
    private static readonly string[] Phrases =
    {
        "Something went wrong",
        "Error loading software",
        "Please contact your IT admin",
    };

    private const int MinimumMatches = 2;

    /// <summary>
    /// A JavaScript expression evaluating to true when the document looks like Fleet's
    /// error page. Expects <c>text</c> to be in scope, holding the body's innerText.
    /// </summary>
    public static string MatchesExpression =>
        "(" + string.Join(" + ", Phrases.Select(p => $"(text.indexOf(\"{p}\") !== -1 ? 1 : 0)")) + $") >= {MinimumMatches}";
}
