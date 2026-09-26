# C# code style

This repository uses [CSharpier](https://csharpier.com/) for consistent C#
formatting. Like Dart's formatter, it chooses the layout automatically, including
line wrapping. It uses conventional C# braces on their own lines, four spaces per
indentation level, and a target line width of 140 characters. The width is a
wrapping target, not a hard limit for strings and other indivisible content.

The formatter version is pinned in [the local tool manifest](../.config/dotnet-tools.json).
[`.editorconfig`](../.editorconfig) defines the shared options, including LF line
endings; [`.gitattributes`](../.gitattributes) keeps those endings on checkout too.
CSharpier also handles operator/comma spacing, using-directive ordering,
and blank lines. The C# IDE0011 rule additionally requires braces around
conditional and loop bodies, including single-statement bodies. Missing braces
are errors in the editor and during builds, enabled by
[`Directory.Build.props`](../Directory.Build.props).

## Change the rules

Edit the `[*.cs]` section in [`.editorconfig`](../.editorconfig):

```ini
max_line_length = 140
csharp_new_line_before_open_brace = all
csharp_prefer_braces = true
dotnet_diagnostic.IDE0011.severity = error
```

`max_line_length` controls CSharpier's wrapping target. The next-line brace option
guides Visual Studio's built-in formatting; CSharpier already uses that placement
for normal blocks. CSharpier still keeps empty bodies and simple auto-properties
compact, and offers no option to expand them. `csharp_prefer_braces = true`
requires braces even for one statement; normal `else if` chains remain intact.
See [Microsoft's IDE0011 documentation](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/ide0011).

After changing rules, run the formatting script below to apply them everywhere.

## Format all C# files

From the repository root on Windows, with the .NET SDK installed:

```powershell
dotnet tool restore
./scripts/Format-CSharp.ps1
```

The script first runs `dotnet format style --diagnostics IDE0011` for the solution
and each standalone test project to add required braces, then runs CSharpier to
apply the final layout. This includes the application, Logger, privileged
projects, and every test harness, including projects outside the solution.
CSharpier respects `.gitignore`
and skips generated C#. [`.csharpierignore`](../.csharpierignore) additionally
excludes vendor files and XML formats such as XAML and project files.

Check the result without changing files:

```powershell
./scripts/Format-CSharp.ps1 -Check
```

For layout alone, `dotnet csharpier format .` and `dotnet csharpier check .` are
still available; these do not add or check missing braces. The
[formatting workflow](../.github/workflows/format.yml) checks layout, while the
[Windows build workflow](../.github/workflows/dotnet-desktop.yml) enforces the
required-brace rule when compiling the app and test harnesses.

## Visual Studio: format on save

1. Open **Extensions > Manage Extensions**, install the official
   [CSharpier extension](https://marketplace.visualstudio.com/items?itemName=csharpier.CSharpier),
   and restart Visual Studio if prompted.
2. Run `dotnet tool restore` from the repository root.
3. Open **Tools > Options > CSharpier > General** and enable
   **Reformat with CSharpier on Save**. The extension supports a solution-specific
   setting as well as a global setting.
4. To insert required braces on save too, open **Analyze > Code Cleanup >
   Configure Code Cleanup**. Include **Add required braces for single-line control
   statements** (called **Add/remove braces** in older versions), and remove
   **Format document** from that profile.
5. Enable **Tools > Options > Text Editor > Code Cleanup > Run Code Cleanup
   profile on save**, selecting the configured profile.
6. Save a `.cs` file. For manual layout formatting, use **Reformat with CSharpier** in
   the editor's context menu.

The extension reads the local tool manifest, so saving uses the same formatter
version as the command line and CI. The extension and its save setting must be
enabled in each developer's Visual Studio installation; `.editorconfig` alone
cannot enable format-on-save.

Use CSharpier as the whitespace formatter and Code Cleanup for adding braces.
For the same reason, `.editorconfig` disables Roslyn's
IDE0055 formatting diagnostic. CSharpier's CI check enforces formatting instead.
Avoid running unrestricted `dotnet format` over CSharpier's output. The repository
script deliberately restricts Roslyn to IDE0011 and runs CSharpier last.

See the official [editor integration](https://csharpier.com/docs/Editors),
[configuration](https://csharpier.com/docs/Configuration), and
[linter integration](https://csharpier.com/docs/IntegratingWithLinters) guidance.
