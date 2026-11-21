$ErrorActionPreference='Stop'
Add-Type -CompilerOptions '/nullable:enable' -Path @("$PSScriptRoot/../src/Route.cs", "$PSScriptRoot/../src/Encounter.cs", "$PSScriptRoot/Tests.cs")
[Tests]::Run([IO.File]::ReadAllText("$PSScriptRoot/observed-timings.json"))
