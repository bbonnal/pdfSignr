using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using pdfSignr.ViewModels;

namespace pdfSignr.Views;

// Page drag-to-reorder and file drag-and-drop
public partial class MainWindow
{
    // Page drag-to-reorder state
    private const double DragThreshold = 8;
    private bool _pageDragPending;
    private bool _pageDragging;
    private PageItem? _dragSourcePage;
    private List<PageItem>? _dragSourcePages; // non-null when dragging a multi-selection
    private Point _dragStartPos;
    private Border? _dragAdorner;
    private int _dropTargetIndex = -1;
    private static readonly Cursor BorderDragCursor = new(StandardCursorType.Hand);
    private Border? _lastCursorBorder;

    // File drag-and-drop state
    private int _dragEnterCount;

    // ═══ Page drag-to-reorder ═══

    private void BeginPageDragFromBorder(PageItem page, Border border, PointerPressedEventArgs e)
    {
        _pageDragPending = true;
        _pageDragging = false;
        _dragSourcePage = page;
        _dragSourcePages = ViewModel.SelectedPageCount > 1
            ? ViewModel.SelectedPages.ToList()
            : null;
        _dragStartPos = e.GetPosition(this);
        _dropTargetIndex = -1;

        e.Pointer.Capture(border);
        border.PointerMoved += OnDragHandleMoved;
        border.PointerReleased += OnDragHandleReleased;
        e.Handled = true;
    }

    private void OnPageAreaPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pageDragging) return; // don't interfere while dragging

        var source = e.Source as Control;
        var page = source != null ? VisualTreeHelpers.FindPageItemFromControl(source) : null;

        if (page is { IsSelected: true })
        {
            var selBorder = FindSelectionBorder(source);
            if (selBorder != null && IsInBorderZone(e.GetPosition(selBorder), selBorder.Bounds))
            {
                if (_lastCursorBorder != selBorder)
                {
                    ClearBorderCursor();
                    selBorder.Cursor = BorderDragCursor;
                    _lastCursorBorder = selBorder;
                }
                return;
            }
        }

        ClearBorderCursor();
    }

    private void ClearBorderCursor()
    {
        if (_lastCursorBorder != null)
        {
            _lastCursorBorder.Cursor = null;
            _lastCursorBorder = null;
        }
    }

    private void OnDragHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_pageDragPending || _dragSourcePage == null) return;

        var pos = e.GetPosition(this);
        var delta = pos - _dragStartPos;

        if (!_pageDragging)
        {
            if (System.Math.Abs(delta.X) < DragThreshold && System.Math.Abs(delta.Y) < DragThreshold)
                return;
            _pageDragging = true;
            ViewModel.IsDraggingPage = true;
            CreateDragAdorner(pos);
        }

        // Move adorner
        if (_dragAdorner != null)
        {
            _dragAdorner.Margin = new Thickness(
                pos.X - _dragAdorner.Width / 2,
                pos.Y - _dragAdorner.Height / 2, 0, 0);
        }

        // Hit-test to find drop target
        UpdateDropTarget(e.GetPosition(PdfScrollViewer));
    }

    private void OnDragHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control handle)
        {
            handle.PointerMoved -= OnDragHandleMoved;
            handle.PointerReleased -= OnDragHandleReleased;
            e.Pointer.Capture(null);
        }

        if (_pageDragging && _dragSourcePage != null && _dropTargetIndex >= 0)
        {
            if (_dragSourcePages != null)
            {
                ViewModel.MovePagesByDrag(_dragSourcePages, _dropTargetIndex);
            }
            else
            {
                int fromIndex = ViewModel.Pages.IndexOf(_dragSourcePage);
                int toIndex = _dropTargetIndex;
                // Adjust target: if moving forward, the removal shifts indices
                if (fromIndex < toIndex) toIndex--;
                if (fromIndex >= 0 && toIndex >= 0 && toIndex < ViewModel.Pages.Count && fromIndex != toIndex)
                {
                    ViewModel.MovePageByDrag(fromIndex, toIndex);
                }
            }
        }

        RemoveDragAdorner();
        ViewModel.ClearDropTargets();
        _pageDragPending = false;
        _pageDragging = false;
        _dragSourcePage = null;
        _dragSourcePages = null;
        _dropTargetIndex = -1;
        ViewModel.IsDraggingPage = false;
        e.Handled = true;
    }

    private void CreateDragAdorner(Point pos)
    {
        if (_dragSourcePage?.Bitmap == null) return;

        // Create a small thumbnail of the page
        double thumbW = DragThumbnailWidth;
        double thumbH = thumbW * (_dragSourcePage.HeightPt / _dragSourcePage.WidthPt);

        var image = new Avalonia.Controls.Image
        {
            Source = _dragSourcePage.Bitmap,
            Width = thumbW,
            Height = thumbH
        };

        Control child;
        if (_dragSourcePages != null && _dragSourcePages.Count > 1)
        {
            // Multi-page drag: show thumbnail with count badge
            var badge = new Border
            {
                Background = Brushes.DodgerBlue,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, -8, -8, 0),
                Child = new TextBlock
                {
                    Text = _dragSourcePages.Count.ToString(),
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeight.Bold
                }
            };
            var grid = new Grid { Width = thumbW, Height = thumbH };
            grid.Children.Add(image);
            grid.Children.Add(badge);
            child = grid;
        }
        else
        {
            child = image;
        }

        _dragAdorner = new Border
        {
            Child = child,
            Background = Brushes.White,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = 8, Color = new Color(100, 0, 0, 0) }),
            Opacity = 0.8,
            Width = thumbW,
            Height = thumbH,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            IsHitTestVisible = false,
            Margin = new Thickness(pos.X - thumbW / 2, pos.Y - thumbH / 2, 0, 0)
        };

        // Add to the top-level Panel (parent of DockPanel)
        if (Content is Panel rootPanel)
        {
            rootPanel.Children.Add(_dragAdorner);
        }
    }

    private void RemoveDragAdorner()
    {
        if (Content is Panel rootPanel)
        {
            if (_dragAdorner != null) rootPanel.Children.Remove(_dragAdorner);
        }
        _dragAdorner = null;
    }

    private void UpdateDropTarget(Point posInScrollViewer)
    {
        _dropTargetIndex = FindInsertionIndex(posInScrollViewer);
        ViewModel.SetDropTarget(_dropTargetIndex);
    }

    // ═══ File drag-and-drop ═══

    private static bool HasPdfFiles(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        return files?.Any(f => f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasPdfFiles(e))
        {
            _dragEnterCount++;
            ViewModel.IsDraggingFile = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (HasPdfFiles(e))
            e.DragEffects = DragDropEffects.Copy;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (--_dragEnterCount <= 0)
        {
            _dragEnterCount = 0;
            ViewModel.IsDraggingFile = false;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dragEnterCount = 0;
        ViewModel.IsDraggingFile = false;

        var files = e.DataTransfer.TryGetFiles();
        var pdfFile = files?.FirstOrDefault(f => f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        var path = pdfFile?.TryGetLocalPath();
        if (path == null) return;

        if (ViewModel.Pages.Count == 0)
        {
            _ = ViewModel.LoadPdfAsync(path);
        }
        else
        {
            int insertIndex = FindInsertionIndex(e.GetPosition(PdfScrollViewer));
            _ = ViewModel.InsertPagesFromFileAsync(path, insertIndex);
        }
    }

    private int FindInsertionIndex(Point posInScrollViewer)
    {
        if (ViewModel.Pages.Count == 0) return 0;

        var itemsControl = (ItemsControl)ZoomTransform.Child!;
        int bestIndex = ViewModel.Pages.Count;
        double minDist = double.MaxValue;

        for (int i = 0; i < ViewModel.Pages.Count; i++)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container == null) continue;

            var topLeft = container.TranslatePoint(new Point(0, 0), PdfScrollViewer);
            var bottomRight = container.TranslatePoint(
                new Point(container.Bounds.Width, container.Bounds.Height), PdfScrollViewer);
            if (topLeft == null || bottomRight == null) continue;

            double midX = (topLeft.Value.X + bottomRight.Value.X) / 2;
            double midY = (topLeft.Value.Y + bottomRight.Value.Y) / 2;

            if (ViewModel.Viewport.IsGridMode)
            {
                double dist = (posInScrollViewer.X - midX) * (posInScrollViewer.X - midX)
                            + (posInScrollViewer.Y - midY) * (posInScrollViewer.Y - midY);
                if (dist < minDist)
                {
                    minDist = dist;
                    bestIndex = posInScrollViewer.X < midX ? i : i + 1;
                }
            }
            else
            {
                double dist = System.Math.Abs(posInScrollViewer.Y - topLeft.Value.Y);
                if (dist < minDist)
                {
                    minDist = dist;
                    bestIndex = i;
                }
                // Also check bottom of last page
                if (i == ViewModel.Pages.Count - 1)
                {
                    double distBottom = System.Math.Abs(posInScrollViewer.Y - bottomRight.Value.Y);
                    if (distBottom < minDist)
                    {
                        bestIndex = i + 1;
                    }
                }
            }
        }

        return System.Math.Min(bestIndex, ViewModel.Pages.Count);
    }
}
