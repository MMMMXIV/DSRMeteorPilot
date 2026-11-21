param(
    [string]$DalamudPath = "$env:APPDATA/XIVLauncher/addon/Hooks/dev",
    [string]$OutputDirectory = "$PSScriptRoot/bin/Release"
)
$ErrorActionPreference = 'Stop'
if ([Environment]::Version.Major -lt 10) { throw 'PowerShell running on .NET 10 or newer is required.' }
Add-Type -Path "$PSHOME/Microsoft.CodeAnalysis.dll"
Add-Type -Path "$PSHOME/Microsoft.CodeAnalysis.CSharp.dll"
$parse = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp14)
$trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
Get-ChildItem "$PSScriptRoot/src/*.cs" | ForEach-Object {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($_.FullName), $parse, $_.Name))
}
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('[assembly: System.Reflection.AssemblyVersion("0.2.5.0")] [assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v10.0")]', $parse))
$refs = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object { $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_.FullName)) }
foreach ($name in @('Dalamud', 'Dalamud.Bindings.ImGui', 'HexaGen.Runtime', 'Lumina', 'Lumina.Excel', 'FFXIVClientStructs', 'InteropGenerator.Runtime')) {
    $path = Join-Path $DalamudPath "$name.dll"
    if (Test-Path $path) { $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path)) }
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithPlatform([Microsoft.CodeAnalysis.Platform]::X64).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable).WithAllowUnsafe($true)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('DSRMeteorPilot', $trees, $refs, $options)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$stream = [IO.MemoryStream]::new()
try {
    $result = $compilation.Emit($stream)
    $result.Diagnostics | ForEach-Object { Write-Output $_.ToString() }
    if (!$result.Success) { throw 'Compilation failed.' }
    [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'DSRMeteorPilot.dll'), $stream.ToArray())
} finally { $stream.Dispose() }
Copy-Item "$PSScriptRoot/DSRMeteorPilot.json" $OutputDirectory
Write-Output "Build succeeded: $OutputDirectory"




