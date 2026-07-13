using CafePOS.Api.Public;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Serves the customer-facing QR ordering page. The encrypted token in the URL is
/// purely for the browser/QR image — the page itself is one static document for every
/// table of every cafe; it reads the token client-side (location.pathname) and calls
/// the token-aware anonymous ordering APIs (see PublicController) with it. Neither the
/// cafe nor the table is ever visible in plain text here.
/// </summary>
[ApiController]
[AllowAnonymous]
public class PublicOrderPageController : ControllerBase
{
    [HttpGet("/order/{token}")]
    public ContentResult Get(string token) => Content(CustomerOrderPage.Html, "text/html; charset=utf-8");
}
