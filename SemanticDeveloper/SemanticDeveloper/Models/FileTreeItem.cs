using System;
using System.Collections.ObjectModel;
using System.IO;

namespace SemanticDeveloper.Models;

public class FileTreeItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public ObservableCollection<FileTreeItem> Children { get; } = new();
    public bool ChildrenInitialized { get; private set; }

    // Simple vector data for icons
    public string IconGeometry => IsDirectory
        ? "M2,6 L8,6 L10,8 L22,8 L22,20 L2,20 Z" // folder-like
        : "M6,4 L18,4 L18,20 L6,20 Z"; // file-like

    // Create item with lazy loading (adds a placeholder to show expander)
    public static FileTreeItem CreateLazy(string path)
    {
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(name))
        {
            try { name = new DirectoryInfo(normalized).Name; }
            catch { name = normalized; }
        }

        var item = new FileTreeItem
        {
            Name = name,
            FullPath = path,
            IsDirectory = Directory.Exists(path)
        };

        if (item.IsDirectory)
        {
            // add placeholder to show expander
            item.Children.Add(new FileTreeItem { Name = "…", FullPath = string.Empty, IsDirectory = false });
            item.ChildrenInitialized = false;
        }
        else
        {
            item.ChildrenInitialized = true;
        }

        return item;
    }

    public void LoadChildrenIfNeeded()
    {
        if (!IsDirectory || ChildrenInitialized)
            return;

        Children.Clear();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(FullPath))
            {
                var name = Path.GetFileName(dir);
                if (name is ".git" or "bin" or "obj" or "node_modules" or ".idea" or ".vs")
                    continue;
                var child = CreateLazy(dir);
                Children.Add(child);
            }

            foreach (var file in Directory.EnumerateFiles(FullPath))
            {
                var child = new FileTreeItem
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    IsDirectory = false,
                    ChildrenInitialized = true
                };
                Children.Add(child);
            }
        }
        catch
        {
            // ignore directories we cannot access
        }

        ChildrenInitialized = true;
    }
}
