#Requires -Version 5.1
<# .SYNOPSIS
Tests the actual diagnostic runner's native process wrapper without opening any audio stream.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$runner = Join-Path (Split-Path $PSScriptRoot -Parent) 'SmartQuietCinema.Tools.VirtualAudioProbe\Invoke-VMwareProbeUpdate.ps1'
$tokens = $null
$errorsFromParser = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errorsFromParser)
if ($errorsFromParser) { throw 'The diagnostic runner failed to parse.' }
$functions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -in 'ConvertTo-NativeArgument', 'Invoke-LabProcess' }, $false))
if ($functions.Count -ne 2) { throw 'Expected both native process helpers.' }
foreach ($definition in $functions) { . ([scriptblock]::Create($definition.Extent.Text)) }

$temporary = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('sqc-pcm-process-' + [guid]::NewGuid().ToString('N')))).FullName
$child = Join-Path $temporary 'native child.ps1'
$ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
try {
    @'
param([string]$Text, [int]$Code)
[Console]::Out.WriteLine($Text)
[Console]::Error.WriteLine('benign usage message')
exit $Code
'@ | Set-Content -LiteralPath $child -Encoding UTF8
    $passed = 0
    $nativeArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $child, '-Text')
    foreach ($code in 0, 7) {
        $text = 'directory with spaces\'
        $result = Invoke-LabProcess $ps51 ($nativeArguments + @($text, '-Code', "$code")) $temporary "exit-$code"
        if ($result.ExitCode -ne $code -or ($result.StdOut -join "`n") -cne $text -or
            (Get-Content -LiteralPath $result.StdErrPath -Raw).Trim() -ne 'benign usage message') { throw "Native status or stream handling failed for exit $code." }
        $passed++
    }
    $text = 'literal " quote'
    $result = Invoke-LabProcess $ps51 ($nativeArguments + @($text, '-Code', '0')) $temporary 'quote'
    if ($result.ExitCode -ne 0 -or ($result.StdOut -join "`n") -cne $text) { throw 'Native argument quoting did not round-trip a literal quote.' }
    $passed++
    "VMware probe process tests: $passed passed, 0 failed, 0 skipped."
} finally {
    foreach ($file in Get-ChildItem -LiteralPath $temporary -File) { [IO.File]::Delete($file.FullName) }
    [IO.Directory]::Delete($temporary)
}
