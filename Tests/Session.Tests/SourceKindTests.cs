using LockscreenGif.Models;

namespace Session.Tests;

internal static class SourceKindTests
{
    public static async Task RunAsync(string root)
    {
        await SelectedSourceAsync(root, LockscreenSourceKind.Video);
        await SelectedSourceAsync(root, LockscreenSourceKind.UserGif);
        await ReferenceOverridesSelectedSourceAsync(root);
        await SelectionChangePreservesSnapshotAsync(root);
    }

    private static async Task SelectedSourceAsync(string root, LockscreenSourceKind sourceKind)
    {
        await using var context = await TestContext.CreateAsync(root, "source-" + sourceKind);
        var source = new LockscreenSource(context.Lockscreen.CurrentImage!.Path, sourceKind);
        context.Lockscreen.CurrentSource = source;

        await context.Service.StartAsync(false, false);

        var apply = context.Lockscreen.Applies.Single();
        Program.Check(
            apply.SourceKind == sourceKind && apply.SourcePath == source.Path,
            $"diagnostic apply retains selected {sourceKind} origin"
        );
    }

    private static async Task ReferenceOverridesSelectedSourceAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "source-reference");
        var source = new LockscreenSource(context.Lockscreen.CurrentImage!.Path, LockscreenSourceKind.Video);
        context.Lockscreen.CurrentSource = source;

        await context.Service.StartAsync(true, false);

        var apply = context.Lockscreen.Applies.Single();
        Program.Check(
            apply.SourceKind == LockscreenSourceKind.BundledGif && apply.SourcePath != source.Path,
            "diagnostic reference apply reports bundled GIF despite selected video"
        );
        Program.Check(context.Lockscreen.CurrentSource == source, "reference apply preserves selected source provenance");
    }

    private static async Task SelectionChangePreservesSnapshotAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "source-changes-during-startup");
        var source = new LockscreenSource(context.Lockscreen.CurrentImage!.Path, LockscreenSourceKind.Video);
        context.Lockscreen.CurrentSource = source;
        var replacementPath = Path.Combine(context.DirectoryPath, "replacement.gif");
        File.Copy(source.Path, replacementPath);
        var replacement = new LockscreenSource(replacementPath, LockscreenSourceKind.UserGif);
        var changed = false;
        context.Service.Changed += (_, _) =>
        {
            if (!changed && context.Service.IsRunning)
            {
                changed = true;
                context.Lockscreen.CurrentSource = replacement;
            }
        };

        await context.Service.StartAsync(false, false);

        var apply = context.Lockscreen.Applies.Single();
        Program.Check(changed && context.Lockscreen.CurrentSource == replacement, "selection changes while diagnostic startup is active");
        Program.Check(
            apply.SourceKind == LockscreenSourceKind.Video && apply.SourcePath == source.Path,
            "diagnostic apply keeps original path and origin when selection changes during startup"
        );
    }
}
