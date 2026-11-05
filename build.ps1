param([switch]$Test, [switch]$Package)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x C# compiler was not found.' }
$mainSource = Join-Path $PSScriptRoot 'src\HistDataExport.cs'
$versionMatch = [regex]::Match((Get-Content -LiteralPath $mainSource -Raw), 'AssemblyFileVersion\("([0-9.]+)"\)')
if (-not $versionMatch.Success) { throw 'AssemblyFileVersion was not found.' }
$binaryVersion = [version]$versionMatch.Groups[1].Value
$versionParts = if ($binaryVersion.Revision -gt 0) { 4 } elseif ($binaryVersion.Build -gt 0) { 3 } else { 2 }
$releaseVersion = $binaryVersion.ToString($versionParts)
$application = Join-Path $PSScriptRoot "eHistdataEnhanced_v$releaseVersion.exe"
$testSource = Join-Path $PSScriptRoot 'src\SelfTests.cs'
$supportSource = Join-Path $PSScriptRoot 'src\ExportSupport.cs'
$inputSource = Join-Path $PSScriptRoot 'src\RememberedInputs.cs'
$dateSource = Join-Path $PSScriptRoot 'src\DateProtocol.cs'
$logSource = Join-Path $PSScriptRoot 'src\DiagnosticLog.cs'
$tagInputSource = Join-Path $PSScriptRoot 'src\TagTextInput.cs'
& $compiler /nologo /debug:pdbonly /target:winexe /platform:x86 /optimize+ "/out:$application" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /reference:System.Xml.dll $mainSource $testSource $supportSource $inputSource $dateSource $logSource $tagInputSource
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output "Built: $application"
if ($Test) {
    $testReport = Join-Path $PSScriptRoot "selftest_v$releaseVersion.txt"
    $process = Start-Process -FilePath $application -ArgumentList @('--self-test', ('"' + $testReport + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw "Tests did not finish within 60 seconds. PID: $($process.Id)" }
    Get-Content -LiteralPath $testReport
    if ($process.ExitCode -ne 0) { throw 'Tests failed.' }
}
if ($Package) {
    $bundle = Join-Path (Split-Path $PSScriptRoot -Parent) "InTouch_HistData_Export_v$releaseVersion.zip"
    $symbols = [IO.Path]::ChangeExtension($application, '.pdb')
    $items = @($application, $symbols, $PSCommandPath, (Join-Path $PSScriptRoot 'src'))
    foreach ($name in @('使用说明.md', "正式版检查_v$releaseVersion.md", "selftest_v$releaseVersion.txt", '界面预览.png', '进度条预览.png')) {
        $candidate = Join-Path $PSScriptRoot $name
        if (Test-Path -LiteralPath $candidate) { $items += $candidate }
    }
    Compress-Archive -LiteralPath $items -DestinationPath $bundle -CompressionLevel Optimal -Force
    Write-Output "Packaged: $bundle"
}
