using System.Net;
using AngleSharp;
using Microsoft.Extensions.Logging;
using OnspringDocs.Options;

namespace OnspringDocs.Scraping;

/// <summary>
/// Authenticates against an Onspring instance using pure HTTP form login
/// and transfers session cookies into a <see cref="CookieContainer"/>.
/// </summary>
internal sealed partial class OnspringAuthenticator(ILogger<OnspringAuthenticator> logger)
{
  public async Task<CookieContainer> AuthenticateAsync(
    ScraperOptions options,
    CancellationToken cancellationToken = default
  )
  {
    cancellationToken.ThrowIfCancellationRequested();

    var cookieContainer = new CookieContainer();
    var baseUri = new Uri(options.BaseUrl);

    using var handler = new HttpClientHandler
    {
      CookieContainer = cookieContainer,
      AllowAutoRedirect = true,
      AutomaticDecompression = DecompressionMethods.All,
      CheckCertificateRevocationList = true,
    };

    using var httpClient = new HttpClient(handler, disposeHandler: false)
    {
      BaseAddress = baseUri,
      Timeout = TimeSpan.FromSeconds(30),
    };

    LogLoggingIn(logger, options.BaseUrl);

    using var loginPageResponse = await httpClient
      .GetAsync(new Uri("/Public/Login", UriKind.Relative), cancellationToken)
      .ConfigureAwait(false);
    loginPageResponse.EnsureSuccessStatusCode();
    var loginHtml = await loginPageResponse.Content
      .ReadAsStringAsync(cancellationToken)
      .ConfigureAwait(false);

    using var context = BrowsingContext.New(Configuration.Default);
    using var doc = await context
      .OpenAsync(req => req.Content(loginHtml), cancellationToken)
      .ConfigureAwait(false);
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

      var type = (input.GetAttribute("type") ?? "text").Trim();
      var placeholder = input.GetAttribute("placeholder") ?? string.Empty;

      if (string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase))
      {
        formFields[name] = input.GetAttribute("value") ?? string.Empty;
      }
      else if (string.Equals(type, "password", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Pass", StringComparison.OrdinalIgnoreCase) ||
               placeholder.Contains("Password", StringComparison.OrdinalIgnoreCase))
      {
        passwordFieldName ??= name;
      }
      else if (name.Contains("User", StringComparison.OrdinalIgnoreCase) ||
               placeholder.Contains("Username", StringComparison.OrdinalIgnoreCase))
      {
        usernameFieldName ??= name;
      }
    }

    usernameFieldName ??= "UserName";
    passwordFieldName ??= "Password";

    formFields[usernameFieldName] = options.Username;
    formFields[passwordFieldName] = options.Password;

    using var content = new FormUrlEncodedContent(formFields);
    using var postResponse = await httpClient
      .PostAsync(new Uri(action, UriKind.RelativeOrAbsolute), content, cancellationToken)
      .ConfigureAwait(false);
    postResponse.EnsureSuccessStatusCode();

    var finalPath = postResponse.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
    if (finalPath.Contains("/Public/Login", StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException(
        "Authentication failed: server returned to /Public/Login. Verify Onspring credentials."
      );
    }

    var cookies = cookieContainer.GetCookies(baseUri);
    if (cookies.Count is 0)
    {
      throw new InvalidOperationException(
        "Authentication succeeded without issuing any session cookies."
      );
    }

    LogAuthenticated(logger, cookies.Count);
    return cookieContainer;
  }

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "Logging in to {BaseUrl}..."
  )]
  private static partial void LogLoggingIn(ILogger logger, string baseUrl);

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "Authenticated successfully via HTTP and captured {CookieCount} session cookie(s)."
  )]
  private static partial void LogAuthenticated(ILogger logger, int cookieCount);
}
