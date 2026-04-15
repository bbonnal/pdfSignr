# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/).

## [1.0.0] - 2026-04-15

### Added
- PDF viewing with adaptive DPI rendering (sharp at any zoom level)
- Text annotations with font selection (Helvetica, Times-Roman, Courier) and live inline editing
- SVG and raster image (PNG/JPG) signature placement
- Annotation manipulation: drag, resize, rotate with visual handles
- Page management: reorder (drag-to-reorder + arrow buttons), insert from other PDFs, remove, export single page
- PDF compression with image resampling presets (Screen / Ebook / Print)
- PDF rasterization (flatten to JPEG images at 72 / 150 / 300 DPI)
- Drag-and-drop file loading and page insertion
- Grid and list view modes for navigating multi-page documents
- Dark and light theme toggle
- Keyboard shortcuts (Ctrl+O, Ctrl+S, Ctrl+0, Ctrl++/-, Delete, Escape)
- Cross-platform support (Windows and Linux) with self-contained binaries
- Bundled Liberation fonts for consistent text rendering without system font dependencies
- CI/CD with GitHub Actions (build + release automation)
