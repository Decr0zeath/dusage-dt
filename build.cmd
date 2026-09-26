@echo off
rem Builds dist\dusage.exe. Needs the .NET 10 SDK; the exe runs on the .NET 10 Desktop Runtime.
rem Exit a running widget first (right-click > Exit), or the old exe can't be replaced.
dotnet publish "%~dp0src\Dusage\Dusage.csproj" -c Release -o "%~dp0dist" -nologo %*
