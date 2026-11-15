@echo off
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

echo Compiling VinylNowPlaying.exe...
"%CSC%" /target:winexe /out:VinylNowPlaying.exe /win32icon:app.ico ^
  /resource:vinyl_base.png,VinylWidget.vinyl_base.png ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll" ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll" ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Xaml.dll" ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll" ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Core.dll" ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll" ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll" ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll" ^
  /r:"C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Runtime.WindowsRuntime\v4.0_4.0.0.0__b77a5c561934e089\System.Runtime.WindowsRuntime.dll" ^
  Program.cs MainWindow.cs MediaManager.cs

if %ERRORLEVEL% equ 0 (
    echo [SUCCESS] VinylNowPlaying.exe successfully created!
) else (
    echo [ERROR] Build failed!
)
