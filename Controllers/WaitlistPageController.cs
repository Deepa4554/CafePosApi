using CafePOS.Api.Public;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Serves the customer-facing "join the waitlist" page. Same shape as
/// PublicOrderPageController, but a separate route rather than a mode-branch inside it — this
/// flow has no menu, no session, nothing in common besides also reading its token from the URL.
/// </summary>
[ApiController]
[AllowAnonymous]
public class WaitlistPageController : ControllerBase
{
    [HttpGet("/waitlist/{token}")]
    public IActionResult Get(string token) => Content(WaitlistJoinPage.Html, "text/html; charset=utf-8");
}
