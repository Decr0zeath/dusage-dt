@echo off
rem Builds dist\dusage.exe: one file with .NET built in. Needs the .NET 10 SDK.
rem Exit a running widget first (right-click > Exit), or the old exe can't be replaced.
dotnet publish "%~dp0src\Dusage\Dusage.csproj" -c Release -o "%~dp0dist" -nologo %*
