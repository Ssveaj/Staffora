# PostToolUse hook: normalises whitespace in a C# file Claude just edited.
# Reads the hook payload (JSON) from stdin. Whitespace-only formatting needs no
# compilation, so it works while the solution does not build.
param([switch]$VerifyOnly)

$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$file = $payload.tool_input.file_path
if (-not $file -or [IO.Path]::GetExtension($file) -ne '.cs' -or -not (Test-Path -LiteralPath $file)) { exit 0 }

$root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { Split-Path (Split-Path $PSScriptRoot) }
$root = [IO.Path]::GetFullPath($root).TrimEnd('\', '/') + '\'
$full = [IO.Path]::GetFullPath($file)
# Windows PowerShell 5.1 has no Path.GetRelativePath; files outside the repo are skipped.
if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { exit 0 }
$relative = $full.Substring($root.Length)

$formatArgs = @('format', 'whitespace', $root, '--folder', '--include', $relative)
if ($VerifyOnly) { $formatArgs += '--verify-no-changes' }

$output = & dotnet @formatArgs 2>&1
if ($LASTEXITCODE -ne 0 -and -not $VerifyOnly) {
    # Non-blocking: report the failure without stopping Claude.
    [Console]::Error.WriteLine("dotnet format failed for ${relative}:`n$output")
    exit 1
}
exit 0
