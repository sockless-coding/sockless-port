using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Sockless.Port.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public string Heading { get; private set; } = "Something went wrong";
    public string Message { get; private set; } = "The request couldn't be completed. Try again, and check the server log if it keeps happening.";
    public string? RequestId { get; private set; }

    public void OnGet(int? code) => Describe(code);

    public void OnPost(int? code) => Describe(code);

    private void Describe(int? code)
    {
        RequestId = code is null ? Activity.Current?.Id ?? HttpContext.TraceIdentifier : null;
        (Heading, Message) = code switch
        {
            404 => ("Page not found", "There's nothing at this address."),
            403 => ("Access denied", "Your account doesn't have access to this page."),
            400 => ("Bad request", "The request wasn't valid. If you submitted a form, reload the page and try again."),
            null => (Heading, Message),
            _ => ($"Error {code}", Message),
        };
    }
}
