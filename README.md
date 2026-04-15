# pdfSignr

Lightweight cross-platform PDF annotation and signing tool built with [Avalonia UI](https://avaloniaui.net/) and .NET 10.

[![Build](https://github.com/bbonnal/pdfSignr/actions/workflows/build.yml/badge.svg)](https://github.com/bbonnal/pdfSignr/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

<!-- TODO: Add screenshot here once available
![pdfSignr screenshot](docs/screenshot-dark.png)
-->

## Features

- **PDF viewing** with adaptive DPI rendering -- sharp at any zoom level
- **Text annotations** with font selection and live inline editing
- **Signature placement** from SVG, PNG, or JPG files
- **Annotation manipulation** -- drag, resize, rotate with visual handles
- **Page management** -- reorder (drag or arrows), insert from other PDFs, remove, export single page
- **PDF compression** -- resample images with presets (screen / ebook / print)
- **PDF rasterization** -- flatten to images at 72 / 150 / 300 DPI
- **Drag and drop** -- drop PDF files to open or insert pages
- **Grid & list views** for navigating multi-page documents
- **Dark and light themes** with runtime toggle
- **Cross-platform** -- runs on Windows and Linux (self-contained, no system dependencies)

## Quick Start

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/bbonnal/pdfSignr.git
cd pdfSignr
dotnet run --project pdfSignr/pdfSignr.csproj
```

## Build & Package

### Windows

```bash
# Single self-contained exe
dotnet publish pdfSignr/pdfSignr.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:EnableCompressionInSingleFile=true

# Or use the build script
packaging/build-win-exe.bat
```

### Linux

```bash
# Single self-contained binary (works on any distro)
dotnet publish pdfSignr/pdfSignr.csproj -c Release -r linux-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:EnableCompressionInSingleFile=true

# Or build an AppImage (requires appimagetool)
packaging/build-appimage.sh
```

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+O` | Open PDF |
| `Ctrl+S` | Save annotated PDF |
| `Ctrl+0` | Fit to width |
| `Ctrl++` / `Ctrl+-` | Zoom in / out |
| `Ctrl+Scroll` | Zoom at cursor |
| `Delete` / `Backspace` | Delete selected annotation |
| `Escape` | Deselect / cancel tool |

## Architecture

Built on **Avalonia 12** with the **CommunityToolkit.Mvvm** MVVM framework. PDF rendering uses **PDFtoImage** (pdfium-based), annotation embedding uses **PDFsharp**, and SVG support is provided by **Svg.Skia**. The custom `PageCanvas` control handles all annotation rendering, hit-testing, rotation, and resize interactions. Bundled **Liberation** fonts ensure consistent text rendering across platforms without system font dependencies.

## License

[MIT](LICENSE)

The bundled [Liberation fonts](https://github.com/liberationfonts/liberation-fonts) are licensed under the [SIL Open Font License](pdfSignr/Assets/Fonts/LICENSE-Liberation.txt).
