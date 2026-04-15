using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using ReverseMarkdown;

var configuration = new ConfigurationBuilder()
  .SetBasePath(Directory.GetCurrentDirectory())
  .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
  .Build();

var baseUrl = configuration["Onspring:BaseUrl"];
var usernameValue = configuration["Onspring:Username"];
var passwordValue = configuration["Onspring:Password"];

if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(usernameValue) || string.IsNullOrWhiteSpace(passwordValue))
{
  Console.WriteLine("Missing required configuration values in appsettings.json (BaseUrl, Username, Password).");
  return;
}

var format = "text";
for (int i = 0; i < args.Length; i++)
{
  if (args[i] == "--format" && i + 1 < args.Length)
  {
    format = args[i + 1].ToLower();
  }
}

var validFormats = new[] { "text", "html", "markdown", "all" };

if (validFormats.Contains(format) is false)
{
  Console.WriteLine($"Invalid format: {format}. Valid formats are: {string.Join(", ", validFormats)}");
  return;
}

var formatsToProcess = format is "all"
  ? ["text", "html", "markdown"]
  : new[] { format };

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger<Program>();

logger.LogInformation("Starting...");

if (Directory.Exists("docs") is false)
{
  logger.LogInformation("Creating docs directory...");
  Directory.CreateDirectory("docs");
}

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = false });

await using var context = await browser.NewContextAsync(new()
{
  BaseURL = baseUrl,
});

var page = await context.NewPageAsync();

logger.LogInformation("Logging in to instance...");

await page.GotoAsync("/Public/Login");

var username = page.GetByPlaceholder(new Regex("Username", RegexOptions.IgnoreCase));
var password = page.GetByPlaceholder(new Regex("Password", RegexOptions.IgnoreCase));
var loginButton = page.GetByRole(AriaRole.Button, new() { NameRegex = new("Login", RegexOptions.IgnoreCase) });

await username.FillAsync(usernameValue);
await password.FillAsync(passwordValue);
await loginButton.ClickAsync();

await page.WaitForURLAsync(new Regex("/Dashboard", RegexOptions.IgnoreCase));

logger.LogInformation("Navigating to Help content...");

await page.GotoAsync("/Help/Content/Home.htm", new() { WaitUntil = WaitUntilState.NetworkIdle });

logger.LogInformation("Expanding all navigation items...");

var navItems = page.Locator(".sidenav li.tree-node:not(.tree-node-leaf)");

for (var i = 0; i < await navItems.CountAsync(); i++)
{
  var navItem = navItems.Nth(i);
  var link = navItem.Locator("> a");

  var ariaExpanded = await link.GetAttributeAsync("aria-expanded");
  var isExpanded = ariaExpanded == "true";

  if (isExpanded)
  {
    continue;
  }

  var toggle = link.Locator(".submenu-toggle-container");

  await toggle.ClickAsync();

  await page.WaitForTimeoutAsync(300);
}

logger.LogInformation("Extracting all navigation links...");

var navLinks = page.Locator(".sidenav a");
var linkAddresses = new List<string>();

foreach (var navLink in await navLinks.AllAsync())
{
  var href = await navLink.GetAttributeAsync("href");

  if (href is null || href.Contains("javascript:void(0)"))
  {
    continue;
  }

  linkAddresses.Add(href);
}

var linkAbsoluteAddresses = linkAddresses.Select(link => link.Replace("..", "/Help")).ToList();

logger.LogInformation("Extracting content from all {Count} navigation links...", linkAbsoluteAddresses.Count);

var converter = new Converter(new Config
{
  GithubFlavored = true,
  RemoveComments = true
});

foreach (var link in linkAbsoluteAddresses)
{
  await page.GotoAsync(link, new() { WaitUntil = WaitUntilState.NetworkIdle });

  var main = page.Locator("#mc-main-content");

  foreach (var currentFormat in formatsToProcess)
  {
    string content;
    string extension;

    if (currentFormat == "text")
    {
      content = await main.InnerTextAsync();
      content = RemoveEmptyNewLines(content);
      extension = "txt";
    }
    else if (currentFormat == "html")
    {
      content = await main.InnerHTMLAsync();
      extension = "html";
    }
    else
    {
      var html = await main.InnerHTMLAsync();
      content = converter.Convert(html);
      extension = "md";
    }

    if (string.IsNullOrWhiteSpace(content))
    {
      continue;
    }

    var pageUrl = new Uri(page.Url);
    var path = pageUrl.AbsolutePath;
    var fileName = path.Replace("/", "_").TrimStart('_').Replace(".htm", string.Empty);
    File.WriteAllText($"docs/{fileName}.{extension}", content);

    logger.LogInformation("Extracted content from {Link} as {Format}", link, currentFormat);
  }
}

static string RemoveEmptyNewLines(string input) => Regex.Replace(input, @"^\s*$\n|\r", "", RegexOptions.Multiline);

