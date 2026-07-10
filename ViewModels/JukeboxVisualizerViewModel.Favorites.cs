using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Models;
using Jukebox.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

// Favorite add/remove/rename and the tree-sync helpers they share.
// See JukeboxVisualizerViewModel.cs for the file-splitting overview.
public partial class JukeboxVisualizerViewModel
{
    #region Fields & Constants
    // Regex for matching texture filenames (e.g. image extensions).
    private static readonly Regex TextureFileRegex = new(
        @"[a-zA-Z0-9_-]+\.(?:jpg|png|bmp|tga)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #endregion

    #region Add / Remove Favorites
    [RelayCommand(CanExecute = nameof(CanAddToFavorites))]
    private async Task AddToFavorites()
    {
        if (SelectedNode is not VisualizerFileViewModel fileVm) return;
        var favFolder = _pathProvider.ProjectMFavoritesDirectory;
        var destPath = Path.Combine(favFolder, Path.GetFileName(fileVm.Path));
        if (fileVm.Path.Equals(destPath, StringComparison.OrdinalIgnoreCase)) return;

        // Confirm before overwrite. The user may have customized the
        // existing favorite — silent overwrite would lose their work.
        if (File.Exists(destPath))
        {
            bool overwrite = await Jukebox.Views.ThreeButtonDialogView.ShowConfirmAsync(
                "File Already in Favorites",
                $"A file named '{Path.GetFileName(fileVm.Path)}' already exists in Favorites.\n" +
                "Overwrite it? This will lose any customizations you made to the favorite.",
                confirmText: "Overwrite",
                cancelText: "Cancel");
            if (!overwrite) return;
        }

        try
        {
            if (!Directory.Exists(favFolder))
            {
                Directory.CreateDirectory(favFolder);
            }

            File.Copy(fileVm.Path, destPath, true);

            // Also copy textures — uses the cached static Regex instead of
            // recompiling on every call.
            string sourceDir = Path.GetDirectoryName(fileVm.Path) ?? "";
            string content = await File.ReadAllTextAsync(fileVm.Path);

            foreach (Match match in TextureFileRegex.Matches(content))
            {
                string textureName = match.Value;
                string sourceTex = Path.Combine(sourceDir, textureName);
                if (File.Exists(sourceTex))
                {
                    string destTex = Path.Combine(favFolder, textureName);
                    File.Copy(sourceTex, destTex, true);
                }
            }

            UpdateTreeForAddedFavorite(destPath);
        }
        catch (Exception ex)
        {
            // REFACTOR: Console.WriteLine → Debug.WriteLine (smell §4.5, §6.5).
            System.Diagnostics.Debug.WriteLine($"[Error] AddToFavorites failed: {ex.Message}");
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Add to Favorites Failed",
                ex.Message);
        }
    }
    private bool CanAddToFavorites() => SelectedNode is VisualizerFileViewModel { IsFavorite: false };

    private void UpdateTreeForAddedFavorite(string destPath)
    {
        var favFolder = _rootNodes.OfType<VisualizerFolderViewModel>().FirstOrDefault(x => x.Name == "Favorites");
        if (favFolder == null)
        {
            var favPath = _pathProvider.ProjectMFavoritesDirectory;
            favFolder = new VisualizerFolderViewModel("Favorites", favPath);
            _rootNodes.Insert(0, favFolder);
        }

        var fileName = Path.GetFileNameWithoutExtension(destPath);
        if (!favFolder.Children.Any(x => x is VisualizerFileViewModel f && f.Path == destPath))
        {
            favFolder.Children.Add(new VisualizerFileViewModel(fileName, destPath));
        }

        lock (_allVisualizerPaths)
        {
            if (!_allVisualizerPaths.Contains(destPath))
                _allVisualizerPaths.Add(destPath);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveFromFavorites))]
    private async Task RemoveFromFavorites()
    {
        if (SelectedNode is not VisualizerFileViewModel fileVm) return;

        var favFolder = _pathProvider.ProjectMFavoritesDirectory;

        if (!fileVm.Path.StartsWith(favFolder, StringComparison.OrdinalIgnoreCase)) return;

        // Confirm before delete since File.Delete is irreversible
        // (bypasses the recycle bin on both Windows and Linux).
        bool confirm = await Jukebox.Views.ThreeButtonDialogView.ShowConfirmAsync(
            "Delete Favorite",
            $"Permanently delete '{Path.GetFileName(fileVm.Path)}' from Favorites?\n" +
            "This cannot be undone.",
            confirmText: "Delete",
            cancelText: "Cancel",
            icon: DialogIconTheme.Warning);
        if (!confirm) return;

        try
        {
            File.Delete(fileVm.Path);
            UpdateTreeForRemovedFavorite(fileVm.Path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] RemoveFromFavorites failed: {ex.Message}");
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Remove Favorite Failed",
                ex.Message);
        }
    }
    private bool CanRemoveFromFavorites() => SelectedNode is VisualizerFileViewModel { IsFavorite: true };

    private void UpdateTreeForRemovedFavorite(string removedPath)
    {
        var favFolder = _rootNodes.OfType<VisualizerFolderViewModel>().FirstOrDefault(x => x.Name == "Favorites");
        if (favFolder != null)
        {
            var fileNode = favFolder.Children.OfType<VisualizerFileViewModel>().FirstOrDefault(x => x.Path == removedPath);
            if (fileNode != null)
            {
                favFolder.Children.Remove(fileNode);
            }
            if (favFolder.Children.Count == 0)
            {
                _rootNodes.Remove(favFolder);
            }
        }

        lock (_allVisualizerPaths)
        {
            _allVisualizerPaths.Remove(removedPath);
            if (SelectedVisualizerPath == removedPath)
            {
                SelectedVisualizerPath = _allVisualizerPaths.FirstOrDefault();
            }
        }
    }
    #endregion

    #region Rename
    [RelayCommand(CanExecute = nameof(CanRenameVisualizer))]
    private async Task RenameVisualizerAsync()
    {
        if (SelectedNode is not VisualizerFileViewModel fileVm || string.IsNullOrEmpty(fileVm.Path)) return;

        var currentName = Path.GetFileNameWithoutExtension(fileVm.Path);
        var newName = await Jukebox.Views.RenameDialogView.ShowAsync(currentName);

        if (!string.IsNullOrWhiteSpace(newName) && newName != currentName)
        {
            try
            {
                var directory = Path.GetDirectoryName(fileVm.Path);
                if (directory == null) return;

                var newPath = Path.Combine(directory, newName + ".milk");
                if (!File.Exists(newPath))
                {
                    File.Move(fileVm.Path, newPath);

                    lock (_allVisualizerPaths)
                    {
                        var index = _allVisualizerPaths.IndexOf(fileVm.Path);
                        if (index >= 0)
                        {
                            _allVisualizerPaths[index] = newPath;
                        }
                    }

                    if (SelectedVisualizerPath == fileVm.Path)
                    {
                        SelectedVisualizerPath = newPath;
                    }

                    fileVm.Name = newName;
                    fileVm.Path = newPath;

                    AddToFavoritesCommand.NotifyCanExecuteChanged();
                    RemoveFromFavoritesCommand.NotifyCanExecuteChanged();
                    RenameVisualizerCommand.NotifyCanExecuteChanged();
                }
            }
            catch (Exception ex)
            {
                // Log and show user-facing error dialog.
                System.Diagnostics.Debug.WriteLine($"[Error] RenameVisualizerAsync failed: {ex.Message}");
                await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                    "Rename Failed",
                    $"Could not rename '{currentName}' to '{newName}'.\n\n{ex.Message}");
            }
        }
    }
    private bool CanRenameVisualizer() => SelectedNode is VisualizerFileViewModel;
    #endregion

    #region Add Current To Favorites
    /// <summary>
    /// Adds the currently-playing visualizer preset (SelectedVisualizerPath)
    /// to the Favorites folder. Distinct from the tree-context-menu
    /// <see cref="AddToFavorites"/> command, which operates on the tree
    /// selection (SelectedNode) - this one lets the user favorite whatever
    /// is currently rendering without first finding it in the tree.
    /// </summary>
    /// <remarks>
    /// Always enabled (no CanExecute gate). Silently bails if:
    ///   - No preset is currently selected (SelectedVisualizerPath is null/empty).
    ///   - The current preset is already in the Favorites folder (no duplicates).
    ///   - The current preset IS itself a favorite (its path is inside the
    ///     Favorites folder).
    /// The "no duplicates" check uses the destination filename - if a file
    /// with the same name already exists in Favorites, the click is a no-op.
    /// This matches the user expectation: "I clicked heart, nothing happened,
    /// so it must already be saved." The Favorites tree node updates
    /// immediately via <see cref="UpdateTreeForAddedFavorite"/> so the user
    /// sees the new entry appear.
    /// </remarks>
    [RelayCommand]
    private async Task AddCurrentToFavoritesAsync()
    {
        string? currentPath = SelectedVisualizerPath;
        if (string.IsNullOrEmpty(currentPath)) return;

        var favFolder = _pathProvider.ProjectMFavoritesDirectory;
        var destPath = Path.Combine(favFolder, Path.GetFileName(currentPath));

        // Already a favorite: the current preset lives inside the Favorites
        // folder. Silently bail.
        if (currentPath.Equals(destPath, StringComparison.OrdinalIgnoreCase)) return;

        // A favorite with the same filename already exists. Silently bail
        // - no overwrite prompt, no duplicate. This is the "make sure any
        // favorite can only be added once" requirement.
        if (File.Exists(destPath)) return;

        try
        {
            if (!Directory.Exists(favFolder))
            {
                Directory.CreateDirectory(favFolder);
            }

            // Copy the preset file. overwrite: false - we already checked
            // above that the file does not exist, but passing false makes
            // the no-duplicates guarantee explicit at the File.Copy level
            // too (throws IOException if it somehow appears between the
            // check and the copy, which we catch below).
            File.Copy(currentPath, destPath, false);

            // Copy textures referenced by the preset - same pattern as
            // AddToFavorites. Uses the cached static Regex.
            string sourceDir = Path.GetDirectoryName(currentPath) ?? "";
            string content = await File.ReadAllTextAsync(currentPath);

            foreach (Match match in TextureFileRegex.Matches(content))
            {
                string textureName = match.Value;
                string sourceTex = Path.Combine(sourceDir, textureName);
                if (File.Exists(sourceTex))
                {
                    string destTex = Path.Combine(favFolder, textureName);
                    // Textures use overwrite: true - if the same texture
                    // name is referenced by multiple Favorites, the latest
                    // copy wins. Textures are typically interchangeable
                    // (image files), and we do not want a stale texture
                    // from an older favorite to break a newer one.
                    File.Copy(sourceTex, destTex, true);
                }
            }

            UpdateTreeForAddedFavorite(destPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] AddCurrentToFavoritesAsync failed: {ex.Message}");
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Add to Favorites Failed",
                ex.Message);
        }
    }
    #endregion
}
