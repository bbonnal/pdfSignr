@echo off
setlocal
set ROOT=%~dp0..
echo Publishing pdfSignr for Windows x64...
dotnet publish "%ROOT%\pdfSignr\pdfSignr.csproj" -p:PublishProfile=win-x64
echo.
echo Output: %ROOT%\publish\win-x64\pdfSignr.exe
