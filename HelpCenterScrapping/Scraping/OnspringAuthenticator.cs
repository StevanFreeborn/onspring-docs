using System.Net;
using System.Text.RegularExpressions;
using HelpCenterScrapping.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace HelpCenterScrapping.Scraping;

/// <summary>
/// Authenticates against an Onspring instance using headless Playwright Chromium
/// and transfers session cookies into a <see cref="CookieContainer"/> for fast HTTP fetching.
/// </summary>
public sealed class OnspringAuthenticator
{
  private readonly ILogger<OnspringAuthenticator> _logger;

  public OnspringAuthenticator(ILogger<OnspringAuthenticator> logger)
  {
    _logger = logger;
  }

  public async Task<CookieContainer> AuthenticateAsync(ScraperOptions options, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();

    _logger.LogInformation("Launching Playwright Chromium (Headless={Headless}) to authenticate...", options.Headless);

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new()
    {
      Headless = options.Headless,
    });

    await using var context = await browser.NewContextAsync(new()
    {
      BaseURL = options.BaseUrl,
    });

    var page = await context.NewPageAsync();

    _logger.LogInformation("Logging in to {BaseUrl}...", options.BaseUrl);
    await page.GotoAsync("/Public/Login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

    var usernameInput = page.GetByPlaceholder(new Regex("Username", RegexOptions.IgnoreCase));
    var passwordInput = page.GetByPlaceholder(new Regex("Password", RegexOptions.IgnoreCase));
    var loginButton = page.GetByRole(AriaRole.Button, new() { NameRegex = new("Login", RegexOptions.IgnoreCase) });

    await usernameInput.FillAsync(options.Username);
    await passwordInput.FillAsync(options.Password);
    await loginButton.ClickAsync();

    await page.WaitForURLAsync(new Regex("/Dashboard", RegexOptions.IgnoreCase));

    var playwrightCookies = await context.CookiesAsync();
    var cookieContainer = new CookieContainer();
    var baseUri = new Uri(options.BaseUrl);

    foreach (var pc in playwrightCookies)
    {
      var domain = string.IsNullOrWhiteSpace(pc.Domain) ? baseUri.Host : pc.Domain;
      var path = string.IsNullOrWhiteSpace(pc.Path) ? "/" : pc.Path;
      var netCookie = new System.Net.Cookie(pc.Name, pc.Value, path, domain)
      {
        Secure = pc.Secure,
        HttpOnly = pc.HttpOnly,
      };
      cookieContainer.Add(baseUri, netCookie);
    }

    _logger.LogInformation("Authenticated successfully and captured {CookieCount} session cookies.", playwrightCookies.Count);
    return cookieContainer;
  }
}
