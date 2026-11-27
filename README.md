# onspring-docs

`onspring-docs` scrapes an Onspring Help Center into deterministic Markdown files for AI agents.

It signs in over HTTP, reads MadCap Flare's TOC metadata directly, fetches topics in parallel, and converts them to GitHub-Flavored Markdown. It builds as a single Native AOT executable with no browser or runtime dependencies.

## Output

Each run replaces the output directory atomically, removing deleted topics. Every file uses UTF-8 without a BOM, LF line endings, and no local timestamps, so repeated runs against the same Help Center build match byte for byte.

- Each topic becomes a kebab-case Markdown file with YAML frontmatter. The converter expands collapsed dropdowns, decodes Cloudflare-obfuscated emails, strips dead relative images, and rewrites internal `.htm` links and anchors to relative `.md` targets.
- `AGENTS.md` explains how AI agents should navigate the corpus.
- `INDEX.md` provides a hierarchical table of contents with audience tags, one-line summaries, and separate sections for release notes and videos.
- `llms.txt` lists primary entry points for quick discovery.
- `manifest.json` catalogs titles, headings, outgoing links, counts, and SHA-256 hashes in sorted JSON.

## Configuration

Copy [`OnspringDocs/appsettings.example.json`](OnspringDocs/appsettings.example.json) to `appsettings.json` and set your instance URL and credentials:

```json
{
  "Onspring": {
    "BaseUrl": "https://your-instance.onspring.com",
    "Username": "your-username",
    "Password": "your-password"
  }
}
```

Settings resolve in order, highest precedence first:

1. Environment variables: `ONSPRING__BASEURL`, `ONSPRING__USERNAME`, `ONSPRING__PASSWORD`
2. `--config <path>`
3. `./appsettings.json`
4. `<exe-dir>/appsettings.json`
5. `<exe-dir>/../appsettings.json`

## Usage

```powershell
onspring-docs [options]
```

| Option                    | Effect                                               |
|:--------------------------|:-----------------------------------------------------|
| `--output <dir>`          | Write output to `<dir>`. Defaults to `docs`.         |
| `--config <path>`         | Load settings from `<path>`.                         |
| `--concurrency <1-32>`    | Set parallel HTTP fetches. Defaults to `12`.         |
| `--exclude-release-notes` | Omit release notes.                                  |
| `--recent-releases <N>`   | Keep the `N` newest release notes plus the overview. |
| `--exclude-videos`        | Omit video pages.                                    |
| `--toc-only`              | Scrape only pages listed in the TOC.                 |
| `--save-raw-html <dir>`   | Save fetched HTML to `<dir>`.                        |
| `-h`, `--help`            | Print help and exit.                                 |

## Build and Publish

Building requires the .NET 11 SDK. Publishing a Native AOT executable on Windows also requires MSVC C++ build tools.

```powershell
dotnet build --configuration Release
dotnet test --configuration Release
dotnet publish OnspringDocs/OnspringDocs.csproj -c Release -r win-x64 -o "$env:USERPROFILE\onspring-docs\bin"
```
