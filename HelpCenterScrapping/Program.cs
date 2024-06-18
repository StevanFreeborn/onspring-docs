using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
var  logger = loggerFactory.CreateLogger<Program>();

logger.LogInformation("Starting...");

if (Directory.Exists("docs") is false)
{
  logger.LogInformation("Creating docs directory...");
  Directory.CreateDirectory("docs");
}

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync();

await using var context = await browser.NewContextAsync(new()
{
  BaseURL = "",
});

var page = await context.NewPageAsync();

logger.LogInformation("Logging in to instance...");

await page.GotoAsync("/Public/Login");

var username = page.GetByPlaceholder(new Regex ("Username", RegexOptions.IgnoreCase));
var password = page.GetByPlaceholder(new Regex ("Password", RegexOptions.IgnoreCase));
var loginButton = page.GetByRole(AriaRole.Button, new() { NameRegex = new("Login", RegexOptions.IgnoreCase) });

await username.FillAsync("");
await password.FillAsync("");
await loginButton.ClickAsync();

await page.WaitForURLAsync(new Regex("/Dashboard", RegexOptions.IgnoreCase));

logger.LogInformation("Navigating to Help content...");

await page.GotoAsync("/Help/Content/Home.htm", new() { WaitUntil = WaitUntilState.NetworkIdle });

logger.LogInformation("Expanding all navigation items...");

var navItems = page.Locator(".sidenav li.tree-node:not(.tree-node-leaf)");

for (var i = 0; i < await navItems.CountAsync(); i++)
{
  var navItem = navItems.Nth(i);
  var toggle = navItem.Locator(".submenu-toggle-container");
  var ariaExpanded = await toggle.GetAttributeAsync("aria-expanded");
  var isExpanded = ariaExpanded == "true";

  if (isExpanded)
  {
    continue;
  }

  await navItem.ClickAsync();
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

logger.LogInformation("Extracting content from all navigation links...");

foreach (var link in linkAbsoluteAddresses)
{
  await page.GotoAsync(link, new() { WaitUntil = WaitUntilState.NetworkIdle });

  var main = page.Locator("#mc-main-content");
  var content = await main.InnerTextAsync();

  if (string.IsNullOrWhiteSpace(content))
  {
    continue;
  }

  var cleanedContent = RemoveEmptyNewLines(content);
  var pageUrl = new Uri(page.Url);
  var path = pageUrl.AbsolutePath;
  var fileName = path.Replace("/", "_").TrimStart('_').Replace(".htm", string.Empty);
  File.WriteAllText($"docs/{fileName}.txt", cleanedContent);

  logger.LogInformation("Extracted content from {Link}", link);
}

await page.PauseAsync();

static string RemoveEmptyNewLines(string input) => Regex.Replace(input, @"^\s*$\n|\r", "", RegexOptions.Multiline);

