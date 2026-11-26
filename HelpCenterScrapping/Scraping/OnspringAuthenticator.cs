using System.Net;
using AngleSharp;
using HelpCenterScrapping.Options;
using Microsoft.Extensions.Logging;

namespace HelpCenterScrapping.Scraping;

/// <summary>
/// Authenticates against an Onspring instance using pure HTTP form login
/// and transfers session cookies into a <see cref="CookieContainer"/>.
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

    var cookieContainer = new CookieContainer();
    var baseUri = new Uri(options.BaseUrl);

    var handler = new HttpClientHandler
    {
      CookieContainer = cookieContainer,
      AllowAutoRedirect = true,
      AutomaticDecompression = DecompressionMethods.All,
    };

    using var httpClient = new HttpClient(handler)
    {
      BaseAddress = baseUri,
      Timeout = TimeSpan.FromSeconds(30),
    };

    _logger.LogInformation("Logging in to {BaseUrl}...", options.BaseUrl);

    using var loginPageResponse = await httpClient.GetAsync("/Public/Login", cancellationToken);
    loginPageResponse.EnsureSuccessStatusCode();
    var loginHtml = await loginPageResponse.Content.ReadAsStringAsync(cancellationToken);

    var context = BrowsingContext.New(Configuration.Default);
    var doc = await context.OpenAsync(req => req.Content(loginHtml), cancellationToken);
    var form = doc.QuerySelector("form#login-form") ?? doc.QuerySelector("form")
      ?? throw new InvalidOperationException("Could not find login form on /Public/Login.");

    var action = form.GetAttribute("action");
    if (string.IsNullOrWhiteSpace(action))
    {
      action = "/Public/Login";
    }

    var formFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? usernameFieldName = null;
    string? passwordFieldName = null;

    foreach (var input in form.QuerySelectorAll("input[name]"))
    {
      var name = input.GetAttribute("name");
      if (string.IsNullOrWhiteSpace(name))
      {
        continue;
      }

      var type = (input.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
      var placeholder = input.GetAttribute("placeholder") ?? string.Empty;

      if (type == "hidden")
      {
        formFields[name] = input.GetAttribute("value") ?? string.Empty;
      }
      else if (type == "password" || name.Contains("Pass", StringComparison.OrdinalIgnoreCase) || placeholder.Contains("Password", StringComparison.OrdinalIgnoreCase))
      {
        passwordFieldName ??= name;
      }
      else if (name.Contains("User", StringComparison.OrdinalIgnoreCase) || placeholder.Contains("Username", StringComparison.OrdinalIgnoreCase))
      {
        usernameFieldName ??= name;
      }
    }

    usernameFieldName ??= "UserName";
    passwordFieldName ??= "Password";

    formFields[usernameFieldName] = options.Username;
    formFields[passwordFieldName] = options.Password;

    using var content = new FormUrlEncodedContent(formFields);
    using var postResponse = await httpClient.PostAsync(action, content, cancellationToken);
    postResponse.EnsureSuccessStatusCode();

    var finalPath = postResponse.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
    if (finalPath.Contains("/Public/Login", StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException("Authentication failed: server returned to /Public/Login. Verify Onspring credentials.");
    }

    var cookies = cookieContainer.GetCookies(baseUri);
    if (cookies.Count == 0)
    {
      throw new InvalidOperationException("Authentication succeeded without issuing any session cookies.");
    }

    _logger.LogInformation("Authenticated successfully via HTTP and captured {CookieCount} session cookie(s).", cookies.Count);
    return cookieContainer;
  }
}
