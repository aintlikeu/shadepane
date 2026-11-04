# screen.ps1 — full-screen capture (GDI BitBlt) without stealing focus, for the capture checks in docs/TESTING.md.
# Usage: powershell -ExecutionPolicy Bypass -File tools\screen.ps1 [-Delay 5]
# Saves pb-capture-HHmmss.png into the CURRENT directory ($PWD) and opens it.
param([int]$Delay = 5)

if (-not ('W.U' -as [type])) {
    Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
}
[W.U]::SetProcessDPIAware() | Out-Null   # otherwise only part of the screen is captured at >100% scaling
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Write-Host "Capturing in $Delay s - open the element in the browser and don't touch anything..."
Start-Sleep -Seconds $Delay

$b   = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g   = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $path = Join-Path $PWD "pb-capture-$(Get-Date -Format HHmmss).png"
    $bmp.Save($path)
    Write-Host "Saved: $path"
} finally {
    $g.Dispose(); $bmp.Dispose()
}
Start-Process $path
