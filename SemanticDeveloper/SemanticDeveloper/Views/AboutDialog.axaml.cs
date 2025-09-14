using System;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SemanticDeveloper.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        try
        {
            InitializeComponent();
            
            // Set the version from assembly info
            VersionText.Text = GetAppVersion();
            
            // No need to manually wire up click event since we're using PointerPressed in XAML
            
            // Make sure the dialog is visible and properly configured
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.CanResize = false;
            
            Console.WriteLine("About dialog initialized successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing about dialog: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }
    
    private void WebsiteLink_Click(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            // Try to open the URL in the default browser
            string url = "https://github.com/semantic-developer/semantic-developer";
            OpenUrl(url);
        }
        catch (Exception ex)
        {
            // Log the error but don't crash
            Console.WriteLine($"Error opening URL: {ex.Message}");
        }
    }
    
    private void BuiltOnLink_Click(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            // Try to open the URL in the default browser
            string url = "https://avaloniaui.net/";
            OpenUrl(url);
        }
        catch (Exception ex)
        {
            // Log the error but don't crash
            Console.WriteLine($"Error opening URL: {ex.Message}");
        }
    }
    
    private void OpenUrl(string url)
    {
        try
        {
            // Cross-platform URL opening
            if (OperatingSystem.IsWindows())
            {
                // Windows
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                // Linux
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = url,
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = url,
                    UseShellExecute = true
                });
            }
            else
            {
                // Fallback for other platforms
                Console.WriteLine($"Unable to open URL on this platform. Please visit {url} manually.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error opening URL: {ex.Message}");
        }
    }
    
    private string GetAppVersion()
    {
        try
        {
            // Get the current assembly version
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            
            // Format the version as a string
            if (version != null)
            {
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
            
            // If version is null, try to get the informational version
            var infoVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (infoVersionAttribute != null)
            {
                return infoVersionAttribute.InformationalVersion;
            }
            
            // If all else fails, return a generic version
            return "1.0";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting app version: {ex.Message}");
            return "Unknown";
        }
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PoweredBy_OnPointerPressedLink_Click(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            // Try to open the URL in the default browser
            string url = "https://developers.openai.com/codex";
            OpenUrl(url);
        }
        catch (Exception ex)
        {
            // Log the error but don't crash
            Console.WriteLine($"Error opening URL: {ex.Message}");
        }
    }
}
