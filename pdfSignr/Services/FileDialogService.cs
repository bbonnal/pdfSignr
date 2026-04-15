using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace pdfSignr.Services;

public class FileDialogService(IStorageProvider storageProvider, Window owner) : IFileDialogService
{
    public async Task<string?> PickOpenFileAsync(string title, string[] patterns)
    {
        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string[] patterns)
    {
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(title) { Patterns = patterns }]
        });
        return file?.TryGetLocalPath();
    }

    public Task ShowErrorAsync(string title, string message)
    {
        return ShowDialogAsync(title, message, confirm: false);
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        return ShowDialogAsync(title, message, confirm: true);
    }

    private async Task<bool> ShowDialogAsync(string title, string message, bool confirm)
    {
        bool result = false;

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowDecorations = WindowDecorations.BorderOnly,
            Background = (IBrush?)Application.Current!.FindResource("SystemControlBackgroundAltHighBrush")
                         ?? Brushes.White
        };

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(24, 24, 24, 16)
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(24, 0, 24, 16)
        };

        if (confirm)
        {
            var cancelBtn = new Button { Content = "Cancel", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancelBtn.Click += (_, _) => { result = false; dialog.Close(); };
            buttonPanel.Children.Add(cancelBtn);

            var okBtn = new Button { Content = "OK", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            okBtn.Click += (_, _) => { result = true; dialog.Close(); };
            buttonPanel.Children.Add(okBtn);
        }
        else
        {
            var okBtn = new Button { Content = "OK", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            okBtn.Click += (_, _) => dialog.Close();
            buttonPanel.Children.Add(okBtn);
        }

        dialog.Content = new StackPanel
        {
            Children = { messageText, buttonPanel }
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
